using System.ComponentModel;
using System.Windows;
using Basalt.Services;

namespace Basalt;

public partial class ProgressWindow : Window
{
    private bool _finished;

    /// <summary>用户点击「取消」时触发，由调用方取消后台任务。</summary>
    public event Action? Cancelled;

    public ProgressWindow() => InitializeComponent();

    public void Update(ExtractProgress p)
    {
        MessageText.Text = p.Message;

        if (p.Phase == ExtractPhase.Scanning)
        {
            Bar.IsIndeterminate = true;
            DetailText.Text = "正在递归扫描目录与文件...";
            return;
        }

        Bar.IsIndeterminate = false;
        double pct = p.FilesTotal == 0 ? 100 : p.FilesDone * 100.0 / p.FilesTotal;
        Bar.Value = Math.Max(0, Math.Min(100, pct));
        DetailText.Text = p.FilesTotal == 0
            ? $"{Models.FileItemFormatter.FormatSize(p.BytesDone)}"
            : $"{p.FilesDone} / {p.FilesTotal} 文件 · {pct:0}% · " +
              $"{Models.FileItemFormatter.FormatSize(p.BytesDone)} / {Models.FileItemFormatter.FormatSize(p.BytesTotal)}";

        if (p.Phase is ExtractPhase.Done or ExtractPhase.Failed)
            Finish(p.Phase == ExtractPhase.Failed);
    }

    public void SetFailed(string message)
    {
        MessageText.Text = message;
        Bar.IsIndeterminate = false;
        Bar.Value = 0;
        Finish(failed: true);
    }

    private void Finish(bool failed)
    {
        _finished = true;
        Title = failed ? "提取失败" : "提取完成";
        CancelButton.Content = "关闭";
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_finished) { Close(); return; }
        RequestCancel();
    }

    /// <summary>任务未结束时禁止直接关窗，避免后台任务失去进度窗口。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_finished)
        {
            e.Cancel = true;
            RequestCancel();
        }
        base.OnClosing(e);
    }

    private void RequestCancel()
    {
        CancelButton.IsEnabled = false;
        MessageText.Text = "正在取消...";
        Cancelled?.Invoke();
    }
}
