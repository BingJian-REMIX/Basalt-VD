using System.Windows;
using System.Windows.Input;
using Basalt.Services;

namespace Basalt;

/// <summary>内置简易 Hex Viewer：只读取前 4 KB，16 字节一行。</summary>
public partial class HexViewerWindow : Window
{
    public HexViewerWindow(string fileName, byte[] head)
    {
        InitializeComponent();
        Title = $"十六进制预览 — {fileName}";
        HeaderText.Text = fileName;
        ContentBox.Text = PreviewDispatcher.FormatHex(head);
        StatusText.Text = $"仅显示前 {head.Length} 字节 · 16 字节/行 · 左侧为偏移，右侧为可打印 ASCII";
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        base.OnKeyDown(e);
    }
}
