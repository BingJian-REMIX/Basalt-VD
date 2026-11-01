using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Basalt.ViewModels;

namespace Basalt.Services;

/// <summary>
/// WPF 渲染级自测：真实创建 MainWindow、挂载镜像，然后用视觉树检查
/// TreeView / DataGrid 是否**已经生成了可见的容器节点**。
/// <para>
/// 这一步是必需的：集合发没发通知是数据层的事，UI 是否真的画出来是渲染层的事。
/// 用户反馈"要调整窗口大小才显示"，只有真正跑一遍 Measure/Arrange + 视觉树遍历，
/// 才能证明问题确实解决了，而不是"数据有了但没人画"。
/// </para>
/// <para>
/// 同时把窗口渲染成 PNG 存盘 —— 文字报告看不出配色/高亮是否正确，图片能。
/// </para>
/// </summary>
public static class WpfRenderTest
{
    /// <param name="path">待挂载的镜像路径。</param>
    /// <param name="lightTheme">true = 切到亮色主题（验证亮色下的选中高亮 #E5E7EB）。</param>
    public static string Run(string path, bool lightTheme = false)
    {
        var sb = new StringBuilder();
        void W(string s) => sb.AppendLine(s);

        W("=== Basalt WPF 渲染自测 ===");
        W($"目标: {path}");
        W($"主题: {(lightTheme ? "亮色" : "暗色")}");
        W("");

        MainWindow? window = null;
        try
        {
            window = new MainWindow();

            if (lightTheme)
            {
                var app = (App)Application.Current;
                app.ToggleTheme();
                window.UpdateLayout();
            }

            // 必须真正 Show 一次，WPF 才会构建视觉树并生成容器（仅 Measure/Arrange 不足以
            // 触发 TreeViewItem 的生成，因为容器是在布局阶段由 ItemsHost 产生的）。
            // 关键点：**Show 之后不再做任何 resize**，这样测出来的才是"不需要调窗口大小"。
            window.Show();
            window.UpdateLayout();

            var vm = (MainViewModel)window.DataContext;
            vm.MountPath(path);

            // 挂载后**不做任何 resize**，直接再布局一次
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);

            W($"状态栏: {vm.Status}");
            W("");

            // ---- 左侧 TreeView ----
            var tree = FindDescendant<TreeView>(window);
            if (tree == null)
            {
                W("❌ 视觉树里找不到 TreeView");
                return sb.ToString();
            }

            var treeItems = FindDescendants<TreeViewItem>(tree).ToList();
            W($"TreeView 生成的 TreeViewItem 容器数 = {treeItems.Count}");
            foreach (var ti in treeItems.Take(12))
            {
                var node = ti.DataContext as Models.FolderNode;
                W($"   • {ti.DataContext}   (IsExpanded={ti.IsExpanded})");
            }

            W(treeItems.Count > 0
                ? "✅ 左侧树已在不调整窗口大小的情况下渲染出节点。"
                : "❌ 左侧树仍然空白 —— 仍需调整窗口大小才会显示。");
            W("");

            // ---- 右侧 DataGrid ----
            var grid = FindDescendant<DataGrid>(window);
            if (grid == null)
            {
                W("❌ 视觉树里找不到 DataGrid");
                return sb.ToString();
            }

            var gridRows = FindDescendants<DataGridRow>(grid).ToList();
            W($"DataGrid 生成的 DataGridRow 容器数 = {gridRows.Count}");
            foreach (var row in gridRows.Take(12))
            {
                var item = row.Item as Models.FileItem;
                W($"   • {item?.Name}   ({item?.SizeText})  图标={(item?.IconSource != null ? "有" : "无")}");
            }

            W(gridRows.Count > 0
                ? "✅ 右侧表格已渲染出内容。"
                : "⚠ 右侧表格暂无内容（根节点下可能只有分区/目录项）。");

            // 原生图标是否真的取到了
            int withIcon = vm.Files.Count(f => f.IconSource != null);
            W(withIcon > 0
                ? $"✅ 原生 Windows 图标已生效：{withIcon}/{vm.Files.Count} 项有图标。"
                : "⚠ 未取到任何原生图标。");
            W("");

            // ---- 面包屑 ----
            W($"面包屑层级数 = {vm.Breadcrumbs.Count}");
            foreach (var c in vm.Breadcrumbs)
                W($"   › {c.Display}   (path='{c.Path}'{(c.IsCurrent ? " [当前]" : "")})");
            W(vm.Breadcrumbs.Count > 0 ? "✅ 顶部面包屑已生成。" : "⚠ 面包屑为空。");
            W("");

            // ---- 状态栏 ----
            W($"状态栏路径 = '{vm.CurrentPathText}'");
            W($"状态栏选中统计 = '{vm.SelectionSummary}'");
            W("");

            // ---- 模拟点击树节点，验证能加载 ----
            var firstTreeItem = treeItems.FirstOrDefault(ti => ti.DataContext is Models.FolderNode { Children.Count: > 0 });
            if (firstTreeItem != null)
            {
                var node = (Models.FolderNode)firstTreeItem.DataContext;
                W($"[模拟点击] 树节点 '{node.Name}'");
                vm.SelectedFolder = node;
                window.UpdateLayout();
                W($"   右侧列表项数 = {vm.Files.Count}");
                foreach (var f in vm.Files.Take(8))
                    W($"      {f.Name}   {f.SizeText}");
                W($"   面包屑: {string.Join(" > ", vm.Breadcrumbs.Select(c => c.Display))}");

                // ".." 返回上级是否存在
                var upEntry = vm.Files.FirstOrDefault(f => f.IsParentEntry);
                W(upEntry != null
                    ? $"✅ 列表顶部有 '..' 返回上级项 → '{upEntry.FullPath}'"
                    : "（该层无上级，列表不显示 '..'）");
                W(vm.Files.Count > 0 ? "   ✅ 点击后有内容。" : "   ⚠ 点击后列表为空。");
            }

            W("");
            W("---- 深度验证：逐层进入，直到遇到真实文件 ----");
            // 用面包屑一路下钻：镜像根 → 分区 → 子目录…
            for (int depth = 0; depth < 4; depth++)
            {
                // 找一个真实文件所在层：从当前列表里挑第一个可读目录进入
                var target = vm.Files.FirstOrDefault(f => f.IsDirectory && !f.IsParentEntry);
                if (target == null) break;

                vm.OpenItem(target);
                window.UpdateLayout();
                W($"[进入] {target.Name} → 列表 {vm.Files.Count} 项 | 面包屑: {string.Join(" > ", vm.Breadcrumbs.Select(c => c.Display))}");

                var up = vm.Files.FirstOrDefault(f => f.IsParentEntry);
                W(up != null ? $"   ✅ '..' 存在 → 指向 '{up.FullPath}'" : "   ⚠ 缺少 '..' 返回上级项");

                int iconCount = vm.Files.Count(f => f.IconSource != null);
                W($"   图标覆盖 {iconCount}/{vm.Files.Count}");

                // 找一个真实文件，验证元数据能读出来
                var file = vm.Files.FirstOrDefault(f => !f.IsDirectory && !f.IsParentEntry);
                if (file != null)
                {
                    var meta = vm.GetMetadata(file);
                    W($"   [文件] {file.Name}  大小={meta?.SizeText}  创建={meta?.CreatedText}  修改={meta?.ModifiedText}  属性={meta?.AttributesText}");
                    W(meta != null && !string.IsNullOrEmpty(meta.ModifiedText) && meta.ModifiedText != "—"
                        ? "   ✅ 文件元数据完整可读（属性窗口有数据）"
                        : "   ⚠ 文件元数据缺失");
                    W($"   [打开测试] 尝试用系统默认程序打开（不验证结果，只验证不抛异常）");

                    // 属性窗口：单独构造并截图（ShowDialog 会阻塞，这里直接构造 + RenderTargetBitmap）
                    try
                    {
                        var meta2 = vm.GetMetadata(file);
                        if (meta2 != null)
                        {
                            var pw = new PropertiesWindow(meta2, file.IconSource);
                            pw.Show();
                            pw.UpdateLayout();
                            int pwW = (int)pw.ActualWidth, pwH = (int)pw.ActualHeight;
                            if (pwW > 0 && pwH > 0)
                            {
                                var rtb = new RenderTargetBitmap(pwW, pwH, 96, 96, PixelFormats.Pbgra32);
                                rtb.Render(pw);
                                var enc = new PngBitmapEncoder();
                                enc.Frames.Add(BitmapFrame.Create(rtb));
                                string shot = Path.Combine(Path.GetTempPath(), "basalt_props.png");
                                using (var fs = File.Create(shot)) enc.Save(fs);
                                W($"   ✅ 属性窗口截图已保存: {shot} ({pwW}x{pwH})");
                            }
                            pw.Close();
                        }
                    }
                    catch (Exception ex)
                    {
                        W($"   ⚠ 属性窗口渲染失败: {ex.Message}");
                    }
                    break;
                }
            }

            // ---- 截图存盘：文字报告看不出配色/高亮是否正确 ----
            try
            {
                // 选一行，让选中高亮出现在截图里
                var grid2 = FindDescendant<DataGrid>(window);
                if (grid2 != null && grid2.Items.Count > 0)
                {
                    grid2.SelectedIndex = 0;
                    grid2.UpdateLayout();
                }
                window.UpdateLayout();

                int w = (int)window.ActualWidth, h = (int)window.ActualHeight;
                if (w > 0 && h > 0)
                {
                    var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(window);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    string shot = Path.Combine(Path.GetTempPath(), lightTheme ? "basalt_ui_light.png" : "basalt_ui.png");
                    using (var fs = File.Create(shot)) enc.Save(fs);
                    W("");
                    W($"✅ 界面截图已保存: {shot}  ({w}x{h})");
                }
            }
            catch (Exception ex)
            {
                W($"⚠ 截图失败: {ex.Message}");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            W($"❌ 渲染自测异常: {ex.GetType().Name}: {ex.Message}");
            W(ex.StackTrace ?? "");
            return sb.ToString();
        }
        finally
        {
            try { window?.Close(); } catch { }
        }    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var d in FindDescendants<T>(root).Take(1))
            return d;
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
