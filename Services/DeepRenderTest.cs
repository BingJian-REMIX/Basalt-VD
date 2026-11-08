using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Basalt.ViewModels;

namespace Basalt.Services;

/// <summary>
/// 新增 UI 的渲染自测：主窗口工具栏/右键菜单、新建磁盘窗口、进度窗口（Loading → 确定进度）。
/// 真实 Show + 截图，暗/亮主题各跑一遍。
/// </summary>
public static class DeepRenderTest
{
    public static string Run(bool lightTheme)
    {
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void W(string s) => sb.AppendLine(s);
        void Check(bool ok, string label, string detail = "")
        {
            W($"   [{(ok ? "PASS" : "FAIL")}] {label}{(detail.Length > 0 ? " — " + detail : "")}");
            if (ok) pass++; else fail++;
        }

        W("=== Basalt 新增 UI 渲染自测 ===");
        W($"主题: {(lightTheme ? "亮色" : "暗色")}");
        W("");

        var windows = new List<Window>();
        try
        {
            if (lightTheme) ((App)Application.Current).ToggleTheme();

            var main = new MainWindow();
            windows.Add(main);
            main.Show();
            main.UpdateLayout();
            main.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

            var buttons = FindDescendants<Button>(main).ToList();
            Check(buttons.Any(b => b.Content is string s && s == "新建虚拟磁盘"),
                "工具栏含「新建虚拟磁盘」按钮", $"共 {buttons.Count} 个按钮");

            var grid = main.FindName("FileGrid") as DataGrid;
            var menuItems = grid?.ContextMenu?.Items.OfType<MenuItem>().ToList() ?? new();
            Check(menuItems.Any(m => m.Header is string s && s == "深度清理"), "右键菜单含「深度清理」");
            Check(menuItems.Any(m => m.Header is string s && s == "提取到本地"), "右键菜单含「提取到本地」");
            var vm = (MainViewModel)main.DataContext;
            var deep = menuItems.FirstOrDefault(m => m.Header is string s && s == "深度清理");
            Check(ReferenceEquals(deep?.Command, vm.DeepCleanCommand), "深度清理命令绑定正确");
            Check(Shot(main, lightTheme ? "basalt_deep_main_light.png" : "basalt_deep_main.png").Length > 0,
                "主窗口截图已生成");

            // ---- 新建虚拟磁盘窗口 ----
            var newDisk = new NewDiskWindow { Owner = main };
            windows.Add(newDisk);
            newDisk.Show();
            newDisk.UpdateLayout();
            var nameBox = newDisk.FindName("NameBox") as TextBox;
            var mbrRadio = newDisk.FindName("MbrRadio") as RadioButton;
            var gptRadio = newDisk.FindName("GptRadio") as RadioButton;
            Check(nameBox?.Text == "BasaltDisk", "磁盘名称默认值");
            Check(mbrRadio?.IsChecked == true && gptRadio != null, "MBR / GPT 两个选项");
            Check(Shot(newDisk, lightTheme ? "basalt_deep_newdisk_light.png" : "basalt_deep_newdisk.png").Length > 0,
                "新建磁盘窗口截图已生成");

            // ---- 进度窗口：Loading 与确定进度两种状态 ----
            var progress = new ProgressWindow { Owner = main };
            windows.Add(progress);
            progress.Update(new ExtractProgress { Phase = ExtractPhase.Scanning, Message = "正在扫描目录树..." });
            var bar = progress.FindName("Bar") as ProgressBar;
            Check(bar?.IsIndeterminate == true, "扫描阶段显示 Loading 动画");
            progress.Update(new ExtractProgress
            {
                Phase = ExtractPhase.Copying,
                Message = "子目录\\b.bin",
                FilesDone = 5,
                FilesTotal = 10,
                BytesDone = 512,
                BytesTotal = 1024
            });
            Check(bar?.IsIndeterminate == false && Math.Abs(bar.Value - 50) < 0.01,
                "复制阶段切换为 50% 确定进度", $"Value={bar?.Value}");
            Check(Shot(progress, lightTheme ? "basalt_deep_progress_light.png" : "basalt_deep_progress.png").Length > 0,
                "进度窗口截图已生成");

            // ---- 深度清理窗口（用临时归档夹具驱动真实扫描） ----
            string zip = Path.Combine(Path.GetTempPath(), $"basalt_deepui_{Guid.NewGuid():N}.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            using (var entryStream = archive.CreateEntry("$Recycle.Bin/old.dat").Open())
                entryStream.Write(new byte[2048]);

            using (var cleanupDisk = new ContainerDiskService(zip))
            {
                var cleanup = new CleanupWindow(cleanupDisk) { Owner = main };
                windows.Add(cleanup);
                cleanup.Show();
                Pump(cleanup, 1500);   // 让后台扫描的 async continuation 有机会跑完
                var total = cleanup.FindName("TotalText") as TextBlock;
                var deleteButton = cleanup.FindName("DeleteButton") as Button;
                Check(total?.Text.Contains("发现") == true, "清理窗口已渲染扫描结果", total?.Text ?? "");
                Check(deleteButton?.IsEnabled == false, "只读归档下删除按钮正确禁用");
                Check(Shot(cleanup, lightTheme ? "basalt_deep_cleanup_light.png" : "basalt_deep_cleanup.png").Length > 0,
                    "清理窗口截图已生成");
            }
            try { File.Delete(zip); } catch { }

            W("");
            W($"TOTAL: {pass} PASS / {fail} FAIL");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            W($"   [FAIL] 渲染自测异常 — {ex}");
            W("");
            W($"TOTAL: {pass} PASS / {fail + 1} FAIL");
            return sb.ToString();
        }
        finally
        {
            foreach (var w in windows) { try { w.Close(); } catch { } }
        }
    }

    /// <summary>泵 WPF 消息循环一段时间，让后台扫描的 async continuation 完成。</summary>
    private static void Pump(Window win, int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(milliseconds),
            DispatcherPriority.Normal, (_, _) => frame.Continue = false, win.Dispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static string Shot(Window win, string fileName)
    {
        win.Show();
        win.UpdateLayout();
        win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        int w = Math.Max(1, (int)Math.Ceiling(win.ActualWidth));
        int h = Math.Max(1, (int)Math.Ceiling(win.ActualHeight));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(win);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        string path = Path.Combine(Path.GetTempPath(), fileName);
        using var fs = File.Create(path);
        encoder.Save(fs);
        return path;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var sub in FindDescendants<T>(child)) yield return sub;
        }
    }
}
