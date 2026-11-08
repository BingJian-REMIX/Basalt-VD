using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Basalt.ViewModels;

namespace Basalt.Services;

/// <summary>
/// 容器 UI 渲染自测：真实创建窗口、挂载容器归档、逐层浏览、并截图。
/// 验证"在 Basalt 内直接展开浏览压缩包/ISO/自解压 exe"这条路径的 UI 表现。
/// </summary>
public static class ContainerRenderTest
{
    public static string Run(string path)
    {
        var sb = new StringBuilder();
        void W(string s) => sb.AppendLine(s);

        W("=== Basalt 容器 UI 渲染自测 ===");
        W($"目标: {path}");
        W("");

        MainWindow? window = null;
        try
        {
            window = new MainWindow();
            window.Show();
            window.UpdateLayout();

            var vm = (MainViewModel)window.DataContext;

            // 直接把归档当"磁盘"挂载（走 DiskEngineFactory → ContainerDiskService）
            vm.MountPath(path);
            window.UpdateLayout();

            W($"状态栏: {vm.Status}");
            W($"根节点: {vm.Roots.FirstOrDefault()?.Name}");

            // 列宽实测：star 宽度是否真的生效
            var dg = FindDescendant<DataGrid>(window);
            if (dg != null)
            {
                W("列宽实测:");
                foreach (var col in dg.Columns)
                    W($"   [{col.Header}] Width={col.Width} ActualWidth={col.ActualWidth}");
                W($"   DataGrid ActualWidth={dg.ActualWidth}");
            }
            W($"根下子目录 {vm.Files.Count(f => f.IsDirectory)} 个，文件 {vm.Files.Count(f => !f.IsDirectory)} 个");
            foreach (var f in vm.Files.Take(10))
                W($"   • {f.Name}   {f.SizeText}   图标={(f.IconSource != null ? "有" : "无")}");
            W("");

            // 逐层进入，找最深的一层
            for (int depth = 0; depth < 4; depth++)
            {
                var dir = vm.Files.FirstOrDefault(f => f.IsDirectory && !f.IsParentEntry);
                if (dir == null) break;
                vm.OpenItem(dir);
                window.UpdateLayout();
                W($"[进入] {dir.Name} → {vm.Files.Count} 项 | 面包屑: {string.Join(" > ", vm.Breadcrumbs.Select(c => c.Display))}");
                W($"   状态栏路径: {vm.CurrentPathText}");
            }
            W("");

            // ---- 若当前是虚拟盘，找一个容器文件双击，验证"展开为容器"这条路径 ----
            var containerFile = vm.Files.FirstOrDefault(f => !f.IsDirectory && !f.IsParentEntry &&
                Path.GetExtension(f.Name).ToLowerInvariant() is ".zip" or ".iso" or ".7z" or ".rar" or ".cab");
            if (containerFile != null)
            {
                W($"---- 展开为容器: {containerFile.Name} ----");
                bool ok = vm.TryOpenAsContainer(containerFile);
                window.UpdateLayout();
                W(ok ? "   ✅ 已展开" : "   ❌ 展开失败");
                W($"   状态栏: {vm.Status}");
                W($"   根节点: {vm.Roots.FirstOrDefault()?.Name}");
                W($"   内容: {vm.Files.Count} 项");
                foreach (var f in vm.Files.Take(6))
                    W($"      • {f.Name}   {f.SizeText}");
                W($"   面包屑: {string.Join(" > ", vm.Breadcrumbs.Select(c => c.Display))}");
            }
            else
            {
                W("（当前列表内无容器文件，跳过展开验证）");
            }
            W("");

            // 截图
            int w = (int)window.ActualWidth, h = (int)window.ActualHeight;
            if (w > 0 && h > 0)
            {
                var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(window);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                string shot = Path.Combine(Path.GetTempPath(), "basalt_container_ui.png");
                using (var fs = File.Create(shot)) enc.Save(fs);
                W($"✅ 截图已保存: {shot} ({w}x{h})");
            }
        }
        catch (Exception ex)
        {
            W($"❌ 异常: {ex.GetType().Name}: {ex.Message}");
            W(ex.StackTrace ?? "");
        }
        finally
        {
            try { window?.Close(); } catch { }
        }

        return sb.ToString();
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var d in FindDescendants<T>(root).Take(1)) return d;
        return null;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root == null) yield break;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in FindDescendants<T>(child)) yield return d;
        }
    }
}
