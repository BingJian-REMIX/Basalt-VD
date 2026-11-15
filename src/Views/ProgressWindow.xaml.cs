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

    /// <summary>切换到「一键精简」模式：显示阶段徽标、重置进度条与取消按钮。</summary>
    public void SetupTrim(string heading = "一键精简系统镜像")
    {
        _finished = false;
        HeadingText.Text = heading;
        Title = "一键精简系统镜像";
        StageBadge.Visibility = Visibility.Visible;
        StageText.Text = TrimStageNames.Of(TrimStage.Preparing);
        MessageText.Text = "正在准备...";
        DetailText.Text = "";
        Bar.IsIndeterminate = true;
        Bar.Value = 0;
        CancelButton.Content = "取消";
        CancelButton.IsEnabled = true;
    }

    /// <summary>精简流水线的阶段进度。阶段徽标 + 阶段文案 + 确定/不确定进度条。</summary>
    public void UpdateTrim(TrimProgress p)
    {
        StageBadge.Visibility = Visibility.Visible;
        StageText.Text = p.StageText;
        MessageText.Text = p.Message;
        DetailText.Text = p.Detail;

        if (p.Percent is int pct)
        {
            Bar.IsIndeterminate = false;
            Bar.Value = Math.Max(0, Math.Min(100, pct));
        }
        else
        {
            Bar.IsIndeterminate = true;
        }

        if (!p.IsFinal) return;

        _finished = true;
        Title = p.Stage switch
        {
            TrimStage.Done => "精简完成",
            TrimStage.Failed => "精简失败",
            TrimStage.Cancelled => "已取消",
            _ => "精简结束"
        };
        HeadingText.Text = p.Stage == TrimStage.Done ? "精简版 ISO 已生成" : Title;
        CancelButton.Content = "关闭";
        CancelButton.IsEnabled = true;
        if (p.Stage != TrimStage.Done)
        {
            Bar.IsIndeterminate = false;
            Bar.Value = 0;
        }
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
