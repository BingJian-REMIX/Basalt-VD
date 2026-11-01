using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Basalt.Common;
using Basalt.Models;
using Basalt.Services;
using Microsoft.Win32;

namespace Basalt.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private IVirtualDisk? _disk;
    private FolderNode? _selectedFolder;
    private FileItem? _selectedFile;
    private string _status = "就绪 — 请选择 VHD / VMDK 文件挂载。";

    // 用 RangeObservableCollection：挂载时 Roots.Add(root) / 卸载时 Roots.Clear() 能立即通知左侧 TreeView 刷新，
    // 不必再靠调整窗口大小触发重排（普通 List<T> 不会触发 CollectionChanged）。
    public RangeObservableCollection<FolderNode> Roots { get; } = new();

    // 同理用 RangeObservableCollection：LoadDirectory 整体重置列表，只发一次通知，UI 立即刷新且不卡顿。
    public RangeObservableCollection<FileItem> Files { get; } = new();

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public FolderNode? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (Set(ref _selectedFolder, value) && value != null)
            {
                // 兜底：确保子目录已加载（某些情况下 TreeViewItem 不会把展开状态写回绑定源）
                value.LoadChildren();
                LoadDirectory(value.FullPath);
                CurrentPath = string.IsNullOrEmpty(value.FullPath) ? value.Name : value.FullPath;
            }
        }
    }

    /// <summary>当前所在路径（用于面包屑显示），左侧树选择或右侧列表双击进入都会更新。</summary>
    public string CurrentPath
    {
        get => _currentPath;
        private set => Set(ref _currentPath, value);
    }
    private string _currentPath = "未挂载";

    public FileItem? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (Set(ref _selectedFile, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public RelayCommand MountCommand { get; }
    public RelayCommand UnmountCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ExtractCommand { get; }

    public MainViewModel()
    {
        MountCommand = new RelayCommand(_ => Mount(), _ => _disk == null);
        UnmountCommand = new RelayCommand(_ => Unmount(), _ => _disk != null);
        RefreshCommand = new RelayCommand(_ => { if (_selectedFolder != null) LoadDirectory(_selectedFolder.FullPath); },
            _ => _selectedFolder != null);
        ExtractCommand = new RelayCommand(_ => Extract(), _ => _selectedFile != null);
    }

    private void Mount()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择虚拟磁盘文件",
            Filter = "虚拟磁盘 (*.vhd;*.vhdx;*.vmdk)|*.vhd;*.vhdx;*.vmdk|VHD (*.vhd;*.vhdx)|*.vhd;*.vhdx|VMDK (*.vmdk)|*.vmdk"
        };
        if (dialog.ShowDialog() != true) return;
        MountPath(dialog.FileName);
    }

    /// <summary>挂载指定路径的虚拟磁盘（供 UI 与无界面自测共用）。</summary>
    public void MountPath(string path)
    {
        bool isVmdk = DiskEngineFactory.IsVmdk(path);
        Status = isVmdk ? $"正在解析 {path} ..." : $"正在挂载 {path} ...";
        try
        {
            var disk = DiskEngineFactory.Create(path, readOnly: false);
            _disk = disk;

            // 注入树懒加载器，再构造根节点
            FolderNode.ChildLoader = p => disk.GetDirectories(p);
            Roots.Clear();
            Files.Reset(Array.Empty<FileItem>());

            var root = new FolderNode
            {
                Name = disk.DriveLetter is char letter ? $"{letter}:\\" : $"{disk.DisplayName}（内存解析）",
                FullPath = disk.DriveLetter is char l ? $"{l}:\\" : ""
            };
            // 先加载子节点再入树：根节点下挂的是分区列表（VMDK）或盘符根目录（VHD）
            root.LoadChildren();
            root.IsExpanded = true;
            Roots.Add(root);
            SelectedFolder = root;

            CommandManager.InvalidateRequerySuggested();
            Status = disk.DriveLetter is char lt
                ? $"已挂载 {path} → {lt}:" + (disk is VhdDiskService { MountedReadOnly: true } ? "（只读）" : "")
                : $"已解析 {path}（纯内存模式，无盘符）";
            // 若存在不可读分区（如 ext4 / Linux），追加说明，让用户知道为什么某些分区列不出文件
            if (disk is VmdkDiskService vmdk && !string.IsNullOrEmpty(vmdk.UnsupportedNote))
                Status += "  ⚠ " + vmdk.UnsupportedNote;
        }
        catch (Exception ex)
        {
            _disk?.Dispose();
            _disk = null;
            Status = $"挂载失败：{ex.Message}";
            // 无界面自测模式下不能弹窗（会阻塞甚至抛异常）
            if (!App.Headless)
                MessageBox.Show(ex.Message, "Basalt — 挂载失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Unmount()
    {
        try
        {
            _disk?.Dispose();
            _disk = null;
            FolderNode.ChildLoader = null;
            Roots.Clear();
            Files.Clear();
            SelectedFolder = null;
            Status = "已卸载。";
            CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception ex)
        {
            Status = $"卸载失败：{ex.Message}";
        }
    }

    /// <summary>窗口关闭时调用：尽力卸载，不抛异常、不阻止关闭。</summary>
    public void UnmountOnExit()
    {
        try { _disk?.Dispose(); } catch { }
    }

    private void LoadDirectory(string path)
    {
        if (_disk == null)
        {
            Files.Reset(Array.Empty<FileItem>());
            return;
        }
        CurrentPath = path;

        var items = new List<FileItem>();
        try
        {
            foreach (var dir in _disk.GetDirectories(path))
            {
                items.Add(new FileItem
                {
                    Name = dir.Name,
                    FullPath = dir.FullPath,
                    SizeText = "<目录>",
                    Icon = "📁"
                });
            }
            items.AddRange(_disk.GetFiles(path));
        }
        catch (Exception ex)
        {
            Status = $"读取目录失败：{ex.Message}";
        }
        // 一次性整体替换 → 只触发一次 CollectionChanged，右侧列表立即刷新。
        Files.Reset(items);
    }

    /// <summary>供右侧文件列表双击目录时调用：进入该目录并刷新列表与面包屑。</summary>
    public void OpenFolder(string path)
    {
        LoadDirectory(path);
    }

    private void Extract()
    {
        if (_selectedFile == null || _disk == null) return;
        var dialog = new SaveFileDialog
        {
            Title = "提取到本地",
            FileName = _selectedFile.Name,
            Filter = "所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            _disk.ExtractFile(_selectedFile.FullPath, dialog.FileName);
            Status = $"已提取到 {dialog.FileName}";
        }
        catch (Exception ex)
        {
            Status = $"提取失败：{ex.Message}";
            MessageBox.Show(ex.Message, "Basalt — 提取失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
