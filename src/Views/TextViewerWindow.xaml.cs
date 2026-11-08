using System.Windows;
using System.Windows.Input;
using Basalt.Models;
using Basalt.Services;

namespace Basalt;

/// <summary>内部文本/代码预览：等宽字体，DynamicResource 跟随亮暗主题。</summary>
public partial class TextViewerWindow : Window
{
    public TextViewerWindow(string fileName, TextPreview result)
    {
        InitializeComponent();
        Title = $"文本预览 — {fileName}";
        HeaderText.Text = fileName;
        ContentBox.Text = result.Text;
        StatusText.Text = $"{result.EncodingName} · {result.LineCount} 行 · {FileItemFormatter.FormatSize(result.BytesRead)}" +
                          (result.Truncated ? " · 已达 2 MB / 5000 行上限，内容已截断" : "");
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        base.OnKeyDown(e);
    }
}
