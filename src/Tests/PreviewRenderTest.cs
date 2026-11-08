using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Basalt.Services;

/// <summary>
/// 内部预览窗口渲染级自测：真实 Show() 三个窗口、遍历视觉树、截图存盘。
/// 证明它们不是"只有逻辑没有界面"，也验证纯内存流转出的 BitmapImage 真的画出来了。
/// </summary>
public static class PreviewRenderTest
{
    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFAAH/q842iQAAAABJRU5ErkJggg==";

    public static string Run(bool lightTheme = false)
    {
        var sb = new StringBuilder();
        void W(string s) => sb.AppendLine(s);
        var failures = new List<string>();
        void Check(bool ok, string label, string detail = "")
        {
            W($"   {(ok ? "✅" : "❌")} {label}{(detail.Length > 0 ? " — " + detail : "")}");
            if (!ok) failures.Add(label);
        }

        W("=== Basalt 内部预览窗口渲染自测 ===");
        W($"主题: {(lightTheme ? "亮色" : "暗色")}");
        W("");

        // 测试里逐个 Close 预览窗口；默认 OnLastWindowClose 会让关掉最后一个窗口时直接结束 Application
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (lightTheme) ((App)Application.Current).ToggleTheme();

        try
        {
            // ---- 文本 ----
            string sample = "Basalt 纯内存文本预览\n" + string.Concat(Enumerable.Range(1, 120).Select(i => $"{i,4}: 等宽字体 / 亮暗主题 / 2MB 上限\n"));
            using (var s = new MemoryStream(Encoding.UTF8.GetBytes(sample)))
            {
                var result = PreviewDispatcher.ReadText(s);
                TextViewerWindow? win = null;
                try
                {
                    win = new TextViewerWindow("sample.md", result);
                    win.Show(); win.UpdateLayout();
                    var boxes = FindDescendants<TextBox>(win).ToList();
                    Check(boxes.Count == 1 && boxes[0].Text.Length > 100, "TextViewerWindow 渲染出等宽文本框",
                        $"{result.LineCount} 行 / {result.EncodingName}");
                    Check(boxes.Count > 0 && boxes[0].FontFamily.ToString().Contains("Consolas"), "等宽字体生效");
                    Check(!string.IsNullOrEmpty(win.Title) && win.Title.Contains("sample.md"), "窗口标题含文件名");
                    SaveShot(win, "basalt_preview_text.png", W);
                }
                finally { win?.Close(); }
            }

            // ---- 图片 ----
            using (var s = new MemoryStream(Convert.FromBase64String(OnePixelPng)))
            {
                var image = PreviewDispatcher.LoadImage(s);
                ImageViewerWindow? win = null;
                try
                {
                    win = new ImageViewerWindow("pic.png", image);
                    win.Show(); win.UpdateLayout();
                    win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
                    win.UpdateLayout();
                    var images = FindDescendants<Image>(win).ToList();
                    Check(images.Count > 0 && images[0].Source != null, "ImageViewerWindow 渲染出内存流 BitmapImage",
                        $"{image.PixelWidth}×{image.PixelHeight}");
                    var texts = FindDescendants<TextBlock>(win).Select(t => t.Text).ToList();
                    Check(texts.Any(t => t.Contains("缩放")), "适应窗口缩放已初始化",
                        texts.FirstOrDefault(t => t.Contains("缩放")) ?? "");
                    SaveShot(win, "basalt_preview_image.png", W);
                }
                finally { win?.Close(); }
            }

            // ---- 十六进制 ----
            var binary = new byte[4096];
            binary[0] = (byte)'M'; binary[1] = (byte)'Z';
            for (int i = 2; i < binary.Length; i++) binary[i] = (byte)(i & 0xFF);
            using (var s = new MemoryStream(binary))
            {
                var head = PreviewDispatcher.ReadHead(s, 4096);
                HexViewerWindow? win = null;
                try
                {
                    win = new HexViewerWindow("data.bin", head);
                    win.Show(); win.UpdateLayout();
                    var boxes = FindDescendants<TextBox>(win).ToList();
                    Check(boxes.Count == 1 && boxes[0].Text.Contains("4D 5A") && boxes[0].Text.Contains("|MZ"),
                        "HexViewerWindow 渲染出前 4KB 十六进制视图", $"{head.Length} 字节");
                    Check(boxes.Count > 0 && boxes[0].Text.Split('\n').Length >= 256, "16 字节/行共 256 行");
                    SaveShot(win, "basalt_preview_hex.png", W);
                }
                finally { win?.Close(); }
            }
        }
        catch (Exception ex)
        {
            Check(false, "预览窗口渲染主流程", ex.ToString());
        }

        W("");
        W(failures.Count == 0 ? "=== 全部通过 PASS ===" : $"=== 失败 {failures.Count} 项 FAIL ===");
        return sb.ToString();
    }

    private static void SaveShot(Window win, string fileName, Action<string> log)
    {
        int w = (int)win.ActualWidth, h = (int)win.ActualHeight;
        if (w <= 0 || h <= 0) { log("   ⚠ 窗口尺寸为 0，跳过截图"); return; }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(win);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        string path = Path.Combine(Path.GetTempPath(), fileName);
        using (var fs = File.Create(path)) enc.Save(fs);
        log($"   ✅ 截图已保存: {path} ({w}x{h})");
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;
            foreach (var sub in FindDescendants<T>(child)) yield return sub;
        }
    }
}
