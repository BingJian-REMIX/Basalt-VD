using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Basalt.Models;
using Basalt.ViewModels;

namespace Basalt;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    /// <summary>最近一次右键命中的行（右键目标未必是当前选中行）。</summary>
    private FileItem? _contextItem;

    public MainWindow()
    {
        DataContext = _vm;
        InitializeComponent();
        _vm.PropertyChanged += Vm_PropertyChanged;
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentPath))
            Title = string.IsNullOrEmpty(_vm.CurrentPath) || _vm.CurrentPath == "未挂载"
                ? "Basalt — 虚拟磁盘管理器"
                : $"Basalt — {_vm.CurrentPath}";
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    protected override void OnStateChanged(EventArgs e)
    {
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "🗗" : "🗖";
        base.OnStateChanged(e);
    }

    // 窗口关闭时安全卸载 VHD，确保盘符消失
    protected override void OnClosing(CancelEventArgs e)
    {
        _vm.UnmountOnExit();
        base.OnClosing(e);
    }

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        var app = (App)Application.Current;
        app.ToggleTheme();
        ((Button)sender).Content = app.IsDarkTheme ? "☀️" : "🌙";
    }

    // ==================== 左侧目录树 ====================

    private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNode node)
        {
            node.IsExpanded = true; // 点击节点即展开
            _vm.SelectedFolder = node;
        }
    }

    // ==================== 右侧文件列表 ====================

    /// <summary>双击：目录 → 进入；文件 → 用系统默认程序打开。</summary>
    private void FileGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var item = ResolveItemUnderMouse(e.OriginalSource) ?? _vm.SelectedFile;
        if (item == null) return;
        _vm.OpenItem(item);
    }

    private void FileGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = e.AddedItems.OfType<FileItem>().ToList();
        _vm.UpdateSelectionSummary(selected);
    }

    /// <summary>Enter 键：目录进入；文件打开。</summary>
    private void FileGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _vm.SelectedFile == null) return;
        _vm.OpenItem(_vm.SelectedFile);
        e.Handled = true;
    }

    /// <summary>从鼠标位置的视觉树回溯到 DataGridRow，取出其绑定的数据项。</summary>
    private static FileItem? ResolveItemUnderMouse(object? originalSource)
    {
        if (originalSource is not DependencyObject obj) return null;
        while (obj != null && obj is not DataGridRow)
            obj = VisualTreeHelper.GetParent(obj);
        return (obj as DataGridRow)?.Item as FileItem;
    }

    // ==================== 面包屑 ====================

    private void Breadcrumb_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path })
            _vm.NavigateTo(path);
    }

    // ==================== 右键菜单 ====================

    private void OpenContext_Click(object sender, RoutedEventArgs e)
    {
        var item = ContextItem();
        if (item != null) _vm.OpenItem(item);
    }

    /// <summary>右键"展开为容器"：把选中的归档文件在 Basalt 内直接展开浏览。</summary>
    private void ExpandContext_Click(object sender, RoutedEventArgs e)
    {
        var item = ContextItem();
        if (item == null || item.IsDirectory) return;
        _vm.ExpandAsContainer(item);
    }

    private void ExtractContext_Click(object sender, RoutedEventArgs e)    {
        var item = ContextItem();
        if (item == null) return;
        if (item.IsDirectory)
        {
            _vm.Status = "暂不支持提取文件夹，请先进入该目录再选择文件。";
            return;
        }
        _vm.SelectedFile = item;
        _vm.ExtractCommand.Execute(null);
    }

    private void PropertiesContext_Click(object sender, RoutedEventArgs e)
    {
        var item = ContextItem();
        if (item != null) ShowProperties(item);
    }

    /// <summary>取右键点击的那一行（优先于当前选中行）。</summary>
    private FileItem? ContextItem() => _contextItem ?? _vm.SelectedFile;

    /// <summary>右键时记录被右击的行 —— 右键目标未必是选中行。</summary>
    private void FileGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _contextItem = ResolveItemUnderMouse(e.OriginalSource);
    }

    // ==================== 属性窗口 ====================

    private void ShowProperties(FileItem item)
    {
        var meta = _vm.GetMetadata(item);
        if (meta == null) return;

        var win = new PropertiesWindow(meta, item.IconSource) { Owner = this };
        win.ShowDialog();
    }
}
