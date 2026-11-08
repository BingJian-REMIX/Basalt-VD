using System.Windows;
using Basalt.Services;

namespace Basalt;

public partial class CleanupWindow : Window
{
    private readonly IVirtualDisk _disk;
    private List<CleanupTarget> _targets = new();

    public CleanupWindow(IVirtualDisk disk)
    {
        _disk = disk;
        InitializeComponent();
        _ = RescanAsync();
    }

    private async Task RescanAsync()
    {
        ScanHint.Text = "正在递归统计目录大小...";
        ScanHint.Visibility = Visibility.Visible;
        DeleteButton.IsEnabled = false;

        // 大目录递归可能很慢，放后台线程，避免窗口假死
        var targets = await Task.Run(() => CleanupService.Scan(_disk));
        _targets = targets;
        TargetList.ItemsSource = _targets;

        long total = _targets.Sum(t => t.Bytes);
        int files = _targets.Sum(t => t.FileCount);
        TotalText.Text = _targets.Count == 0
            ? "未发现可清理的系统目录"
            : $"发现 {Models.FileItemFormatter.FormatSize(total)} 可回收内容 — {files} 个文件 / {_targets.Count} 个目录";

        ScanHint.Visibility = Visibility.Collapsed;
        bool canDelete = CleanupService.CanDelete(_disk);
        DeleteButton.IsEnabled = canDelete && _targets.Any(t => t.FileCount > 0);
        HintText.Text = canDelete
            ? "删除会清空所选目录中的文件（保留目录本身）；占用中或无权限的文件会自动跳过。"
            : CleanupService.DeleteBlockedReason(_disk);
    }

    private void Rescan_Click(object sender, RoutedEventArgs e) => _ = RescanAsync();

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var selected = _targets.Where(t => t.IsSelected && t.FileCount > 0).ToList();
        if (selected.Count == 0) return;

        long total = selected.Sum(t => t.Bytes);
        string list = string.Join("\n", selected.Select(t => "  - " + t.Display + "  " + t.SizeText));
        var answer = MessageBox.Show(this,
            "警告：此操作非常危险，可能导致不可逆的数据丢失！\n\n" +
            "即将永久删除以下目录中的全部文件（不使用回收站）：\n" +
            list + $"\n\n合计 {Models.FileItemFormatter.FormatSize(total)}。确认继续？",
            "Basalt — 深度清理确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            DeleteButton.IsEnabled = false;
            CleanupResult result = CleanupService.Delete(_disk, selected);
            if (!App.Headless)
                MessageBox.Show(this, result.Summary, "Basalt — 清理完成",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            _ = RescanAsync();
        }
        catch (Exception ex)
        {
            HintText.Text = "清理失败：" + ex.Message;
        }
    }
}
