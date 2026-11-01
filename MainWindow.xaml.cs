using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Basalt.Models;
using Basalt.ViewModels;

namespace Basalt;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        DataContext = _vm;
        InitializeComponent();
        _vm.PropertyChanged += Vm_PropertyChanged;
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentPath))
            Breadcrumb.Text = _vm.CurrentPath;
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

    private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FolderNode node)
            _vm.SelectedFolder = node;
    }

    // 右侧列表双击目录项 → 进入该目录（与左侧树导航等价）
    private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResolveItemUnderMouse(e.OriginalSource) is { SizeText: "<目录>" } item)
            _vm.OpenFolder(item.FullPath);
    }

    // 键盘 Enter 也可进入目录
    private void FileList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm.SelectedFile is { SizeText: "<目录>" } item)
        {
            _vm.OpenFolder(item.FullPath);
            e.Handled = true;
        }
    }

    /// <summary>从鼠标位置的视觉树回溯到 ListViewItem，取出其绑定的数据项。</summary>
    private static FileItem? ResolveItemUnderMouse(object? originalSource)
    {
        if (originalSource is not DependencyObject obj) return null;
        while (obj != null && obj is not ListViewItem)
            obj = VisualTreeHelper.GetParent(obj);
        return (obj as ListViewItem)?.DataContext as FileItem;
    }

    // 点击表头排序
    private GridViewColumnHeader? _lastHeader;
    private ListSortDirection _lastDirection = ListSortDirection.Ascending;

    private void GridViewColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not GridViewColumnHeader header || header.Role == GridViewColumnHeaderRole.Padding)
            return;

        var column = header.Column?.Header as string;
        string? property = column switch
        {
            "文件名" => nameof(FileItem.Name),
            "大小" => null, // SizeText 是格式化字符串，直接按字符串排即可
            "修改时间" => nameof(FileItem.ModifiedText),
            _ => null
        };

        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(_vm.Files);
        view.SortDescriptions.Clear();

        var direction = header == _lastHeader && _lastDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;

        if (property != null)
            view.SortDescriptions.Add(new SortDescription(property, direction));
        else if (column == "大小")
            view.CustomSort = direction == ListSortDirection.Ascending
                ? Comparer<FileItem>.Create((a, b) => a.SizeText.CompareTo(b.SizeText))
                : Comparer<FileItem>.Create((a, b) => b.SizeText.CompareTo(a.SizeText));
        else
            view.CustomSort = null;

        if (property != null) view.CustomSort = null;
        _lastHeader = header;
        _lastDirection = direction;
    }
}
