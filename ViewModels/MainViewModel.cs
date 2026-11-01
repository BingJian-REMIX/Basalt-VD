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
    /// <summary>
    /// 磁盘栈：在容器（zip/iso…）里再展开容器时可逐层回退。
    /// 每一项记录"上一层磁盘 + 当时所在的路径"。
    /// </summary>
    private readonly Stack<(IVirtualDisk Disk, string Path)> _diskStack = new();

    private IVirtualDisk? _disk;
    private FolderNode? _selectedFolder;
    private FileItem? _selectedFile;
    private string _status = "就绪 — 请选择 VHD / VMDK 文件挂载。";

    // 用 RangeObservableCollection：挂载时 Roots.Add(root) / 卸载时 Roots.Clear() 能立即通知左侧 TreeView 刷新，
    // 不必再靠调整窗口大小触发重排（普通 List<T> 不会触发 CollectionChanged）。
    public RangeObservableCollection<FolderNode> Roots { get; } = new();

    // 同理用 RangeObservableCollection：LoadDirectory 整体重置列表，只发一次通知，UI 立即刷新且不卡顿。
    public RangeObservableCollection<FileItem> Files { get; } = new();

    /// <summary>顶部路径面包屑（从镜像根到当前目录）。</summary>
    public RangeObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = new();

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
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
            {
                UpdateSelectionSummary();
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>状态栏右侧：选中项统计（文件数 / 文件夹数）。</summary>
    public string SelectionSummary
    {
        get => _selectionSummary;
        private set => Set(ref _selectionSummary, value);
    }
    private string _selectionSummary = "";

    /// <summary>状态栏：当前路径。</summary>
    public string CurrentPathText
    {
        get => _currentPathText;
        private set => Set(ref _currentPathText, value);
    }
    private string _currentPathText = "未挂载";

    public RelayCommand MountCommand { get; }
    public RelayCommand UnmountCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand ExtractCommand { get; }
    public RelayCommand GoUpCommand { get; }

    public MainViewModel()
    {
        MountCommand = new RelayCommand(_ => Mount(), _ => _disk == null);
        UnmountCommand = new RelayCommand(_ => Unmount(), _ => _disk != null);
        RefreshCommand = new RelayCommand(_ => { if (_selectedFolder != null) LoadDirectory(_selectedFolder.FullPath); },
            _ => _selectedFolder != null);
        ExtractCommand = new RelayCommand(_ => Extract(), _ => _selectedFile != null);
        GoUpCommand = new RelayCommand(_ => GoUp(), _ => CanGoUp());
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
            Breadcrumbs.Reset(Array.Empty<BreadcrumbItem>());

            var root = new FolderNode
            {
                Name = RootLabel(disk),
                FullPath = disk.DriveLetter is char l ? $"{l}:\\" : "",
                IconSource = IconProvider.FolderIcon
            };
            // 先加载子节点再入树：根节点下挂的是分区列表（VMDK）或盘符根目录（VHD）
            root.LoadChildren();
            root.IsExpanded = true;
            Roots.Add(root);
            SelectedFolder = root;

            CommandManager.InvalidateRequerySuggested();
            Status = disk switch
            {
                ContainerDiskService c => $"已展开 {Path.GetFileName(c.DisplayName)}（{ContainerDetector.DisplayName(c.Format)}）",
                _ when disk.DriveLetter is char lt =>
                    $"已挂载 → {lt}:" + (disk is VhdDiskService { MountedReadOnly: true } ? "（只读）" : ""),
                _ => "已解析（内存模式，无盘符）"
            };
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

    /// <summary>根节点显示名：VHD 用盘符，VMDK 标"内存解析"，容器标归档格式。</summary>
    private static string RootLabel(IVirtualDisk disk) => disk switch
    {
        ContainerDiskService c => $"{Path.GetFileName(c.DisplayName)}（{ContainerDetector.DisplayName(c.Format)}）",
        _ => disk.DriveLetter is char letter ? $"{letter}:\\" : $"{disk.DisplayName}（内存解析）"
    };

    private void Unmount()
    {
        try
        {
            while (_diskStack.Count > 0)
                _diskStack.Pop().Disk.Dispose();
            _disk?.Dispose();
            _disk = null;
            FolderNode.ChildLoader = null;
            Roots.Clear();
            Files.Reset(Array.Empty<FileItem>());
            Breadcrumbs.Reset(Array.Empty<BreadcrumbItem>());
            SelectedFolder = null;
            CurrentPath = "未挂载";
            CurrentPathText = "未挂载";
            SelectionSummary = "";
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
            // 顶部插入 ".." 返回上级
            // 在容器根目录时，".." 的语义是"退回上一个磁盘"（VMDK/VHD → 容器 → 容器内层）
            bool popsStack = _diskStack.Count > 0 && _disk is ContainerDiskService && path.Length == 0;
            string? parent = popsStack ? null : _disk.GetParentPath(path);
            if (popsStack || parent != null)
            {
                items.Add(new FileItem
                {
                    Name = "..",
                    FullPath = parent ?? "",
                    SizeText = "",
                    IconSource = IconProvider.FolderIcon,
                    IsDirectory = true,
                    IsParentEntry = true,
                    PopsDiskStack = popsStack
                });
            }

            foreach (var dir in _disk.GetDirectories(path))
            {
                items.Add(new FileItem
                {
                    Name = dir.Name,
                    FullPath = dir.FullPath,
                    SizeText = "<目录>",
                    IconSource = dir.IconSource ?? IconProvider.FolderIcon,
                    IsDirectory = true
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

        UpdateBreadcrumb(path);
        UpdateSelectionSummary();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>重建顶部面包屑。</summary>
    private void UpdateBreadcrumb(string path)
    {
        if (_disk == null)
        {
            Breadcrumbs.Reset(Array.Empty<BreadcrumbItem>());
            CurrentPathText = "未挂载";
            return;
        }

        var crumbs = _disk.BuildBreadcrumb(path);
        Breadcrumbs.Reset(crumbs);

        // 状态栏显示引擎原始路径（与面包屑的友好名互补，不重复）：
        // VMDK 为 "0\EFI\Boot" 伪路径，VHD 为 "Z:\Users" 真实路径
        CurrentPathText = string.IsNullOrEmpty(path) ? crumbs.FirstOrDefault()?.Display ?? "未挂载" : path;
    }

    /// <summary>面包屑跳转：点击某一级 → 跳到该层级。</summary>
    public void NavigateTo(string path)
    {
        if (_disk == null) return;
        LoadDirectory(path);
    }

    /// <summary>双击列表项：目录 → 进入；容器文件 → 展开浏览；普通文件 → 用系统默认程序打开。</summary>
    public void OpenItem(FileItem? item)
    {
        if (item == null || _disk == null) return;

        if (item.IsDirectory)
        {
            // ".." 在容器根目录 → 退回上一个磁盘
            if (item.IsParentEntry && item.PopsDiskStack)
            {
                PopDiskStack();
                return;
            }
            LoadDirectory(item.FullPath);
            return;
        }

        // 容器文件（zip/iso/rar/7z/SFX…）→ 在 Basalt 内展开浏览
        if (TryOpenAsContainer(item))
            return;

        try
        {
            _disk.OpenWithDefaultApp(item.FullPath);
            Status = $"已打开 {item.Name}";
        }
        catch (Exception ex)
        {
            // 未知格式文件往往没有关联程序 —— 此时直接弹属性窗口更有用
            Status = $"无法用默认程序打开（{ex.Message}）";
            if (!App.Headless)
            {
                var meta = GetMetadata(item);
                if (meta != null)
                    new PropertiesWindow(meta, item.IconSource) { Owner = Application.Current.MainWindow }.ShowDialog();
            }
        }
    }

    /// <summary>
    /// 尝试把文件当作容器展开浏览。成功返回 true（已切换视图），否则 false。
    /// </summary>
    public bool TryOpenAsContainer(FileItem item) => TryOpenAsContainer(item, pushStack: true);

    /// <summary>右键菜单用的展开入口：展开失败时给出明确提示。</summary>
    public void ExpandAsContainer(FileItem item)
    {
        if (TryOpenAsContainer(item, pushStack: true)) return;
        Status = $"{item.Name} 不是可展开的归档文件（zip/iso/7z/rar/cab/自解压程序）。";
    }

    private bool TryOpenAsContainer(FileItem item, bool pushStack)
    {
        try
        {
            // 先实体化：VMDK/容器内的文件必须先导出到真实路径，7-Zip 才能读
            string real = _disk!.MaterializeToTemp(item.FullPath);

            var fmt = ContainerDetector.DetectFile(real);
            if (fmt == ContainerFormat.None)
                return false;

            // 自解压 exe 要确认内嵌的确实是归档
            if (fmt == ContainerFormat.SelfExtracting && !ContainerDetector.IsContainerFile(real))
                return false;

            var container = new ContainerDiskService(real);

            // 把当前磁盘压栈，容器根的 ".." 可退回
            _diskStack.Push((_disk, CurrentPath));
            _disk = container;
            FolderNode.ChildLoader = p => container.GetDirectories(p);

            Roots.Clear();
            var root = new FolderNode
            {
                Name = $"{Path.GetFileName(real)}（{ContainerDetector.DisplayName(fmt)}）",
                FullPath = "",
                IconSource = IconProvider.FolderIcon
            };
            root.LoadChildren();
            root.IsExpanded = true;
            Roots.Add(root);
            SelectedFolder = root;

            Status = $"已展开 {Path.GetFileName(real)}（{ContainerDetector.DisplayName(fmt)}）";
            return true;
        }
        catch (Exception ex)
        {
            Status = $"展开失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>退回上一个磁盘（从容器返回 VMDK/VHD）。</summary>
    private void PopDiskStack()
    {
        if (_diskStack.Count == 0) return;

        var (disk, path) = _diskStack.Pop();
        _disk?.Dispose();
        _disk = disk;
        FolderNode.ChildLoader = p => disk.GetDirectories(p);

        Roots.Clear();
        var root = new FolderNode
        {
            Name = RootLabel(disk),
            FullPath = disk.DriveLetter is char l ? $"{l}:\\" : "",
            IconSource = IconProvider.FolderIcon
        };
        root.LoadChildren();
        root.IsExpanded = true;
        Roots.Add(root);
        SelectedFolder = root;

        LoadDirectory(path);
        Status = "已返回上一个磁盘。";
    }

    /// <summary>返回上级目录。</summary>
    private void GoUp()
    {
        if (_disk == null) return;
        // 容器根目录 → 退回上一个磁盘
        if (_diskStack.Count > 0 && _disk is ContainerDiskService && CurrentPath.Length == 0)
        {
            PopDiskStack();
            return;
        }
        string? parent = _disk.GetParentPath(CurrentPath);
        if (parent == null) return;
        LoadDirectory(parent);
    }

    private bool CanGoUp()
    {
        if (_disk == null) return false;
        if (_diskStack.Count > 0 && _disk is ContainerDiskService && CurrentPath.Length == 0) return true;
        return _disk.GetParentPath(CurrentPath) != null;
    }

    /// <summary>读取选中项的完整元数据（属性窗口用）。</summary>
    public FileMetadata? GetMetadata(FileItem? item)
    {
        if (item == null || _disk == null) return null;
        if (item.IsParentEntry) return null;
        return _disk.GetMetadata(item.FullPath);
    }

    /// <summary>状态栏：更新选中项统计。</summary>
    public void UpdateSelectionSummary(IEnumerable<FileItem>? selected = null)
    {
        var items = selected?.ToList()
                    ?? (SelectedFile != null ? new List<FileItem> { SelectedFile } : new List<FileItem>());
        int files = items.Count(i => !i.IsDirectory);
        int dirs = items.Count(i => i.IsDirectory);
        SelectionSummary = items.Count == 0
            ? "未选中任何项"
            : $"已选 {files} 个文件、{dirs} 个文件夹";
    }

    private void Extract()
    {
        if (_selectedFile == null || _disk == null) return;
        if (_selectedFile.IsDirectory)
        {
            Status = "暂不支持提取文件夹，请先进入该目录再选择文件。";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "提取文件到本地",
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
            if (!App.Headless)
                MessageBox.Show(ex.Message, "Basalt — 提取失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
