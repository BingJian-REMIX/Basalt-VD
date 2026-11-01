using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Basalt.ViewModels;

namespace Basalt.Services;

/// <summary>
/// WPF 渲染级自测：真实创建 MainWindow、挂载镜像，然后用视觉树检查
/// TreeView / ListView 是否**已经生成了可见的容器节点**。
/// <para>
/// 这一步是必需的：集合发没发通知是数据层的事，UI 是否真的画出来是渲染层的事。
/// 用户反馈"要调整窗口大小才显示"，只有真正跑一遍 Measure/Arrange + 视觉树遍历，
/// 才能证明问题确实解决了，而不是"数据有了但没人画"。
/// </para>
/// </summary>
public static class WpfRenderTest
{
    public static string Run(string path)
    {
        var sb = new StringBuilder();
        void W(string s) => sb.AppendLine(s);

        W("=== Basalt WPF 渲染自测 ===");
        W($"目标: {path}");
        W("");

        MainWindow? window = null;
        try
        {
            window = new MainWindow();

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

            // ---- 右侧 ListView ----
            var list = FindDescendant<ListView>(window);
            if (list == null)
            {
                W("❌ 视觉树里找不到 ListView");
                return sb.ToString();
            }

            var listItems = FindDescendants<ListViewItem>(list).ToList();
            W($"ListView 生成的 ListViewItem 容器数 = {listItems.Count}");
            foreach (var li in listItems.Take(12))
                W($"   • {li.DataContext}");

            W(listItems.Count > 0
                ? "✅ 右侧列表已渲染出内容。"
                : "⚠ 右侧列表暂无内容（根节点下可能只有分区/目录项）。");
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
                W(vm.Files.Count > 0 ? "   ✅ 点击后有内容。" : "   ⚠ 点击后列表为空。");
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
