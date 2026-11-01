using System.Text;
using System.Windows;
using Basalt.Models;
using Basalt.ViewModels;

namespace Basalt.Services;

/// <summary>
/// 无界面自测：直接驱动 MainViewModel，验证"左侧树 + 右侧列表"这条 UI 数据链是否真的通了。
/// <para>
/// 之前用户反馈两个现象：挂载后左侧树空白（必须调整窗口大小才显示）、点目录进不去。
/// 根因是 Roots / FolderNode.Children 用了普通 List&lt;T&gt;，不触发 CollectionChanged。
/// 本自测会真实地订阅 CollectionChanged 事件，确认 UI 能在挂载瞬间收到通知，
/// 并模拟"选中分区节点 → 加载目录""双击目录 → 进入"这两条交互路径。
/// </para>
/// </summary>
public static class UiSelfTest
{
    public static string Run(string path)
    {
        var sb = new StringBuilder();
        void W(string s) => sb.AppendLine(s);

        W($"=== Basalt UI 数据链自测 ===");
        W($"目标: {path}");
        W("");

        var vm = new MainViewModel();

        // 记录集合通知次数 —— 这正是"要不要调整窗口大小才刷新"的关键指标
        int rootsNotify = 0, filesNotify = 0, childNotify = 0;
        vm.Roots.CollectionChanged += (_, _) => rootsNotify++;
        vm.Files.CollectionChanged += (_, _) => filesNotify++;

        try
        {
            vm.MountPath(path);
        }
        catch (Exception ex)
        {
            W($"挂载抛异常: {ex.GetType().Name}: {ex.Message}");
            return sb.ToString();
        }

        W($"状态栏: {vm.Status}");
        W($"Roots.Count = {vm.Roots.Count}   (Roots 通知次数 = {rootsNotify})");
        W($"Files.Count = {vm.Files.Count}  (Files 通知次数 = {filesNotify})");
        W("");

        if (vm.Roots.Count == 0)
        {
            W("❌ Roots 为空 —— 左侧树不会显示任何东西（这就是调整窗口大小才出现的原因）。");
            return sb.ToString();
        }

        var root = vm.Roots[0];
        W($"根节点: Name='{root.Name}'  FullPath='{root.FullPath}'");
        W($"根节点子项数: {root.Children.Count}");
        root.Children.CollectionChanged += (_, _) => childNotify++;

        foreach (var c in root.Children)
            W($"   └─ {c.Name}   (FullPath='{c.FullPath}')");
        W("");

        // 关键断言 1：挂载瞬间 Roots 必须已收到 CollectionChanged 通知（否则 UI 不刷新）
        if (rootsNotify == 0)
            W("❌ Roots 在挂载过程中没有发出任何 CollectionChanged —— TreeView 不会自动刷新。");
        else
            W($"✅ Roots 在挂载时发出了 {rootsNotify} 次 CollectionChanged，TreeView 会立即刷新。");

        // 关键断言 2：根节点子项必须已加载
        if (root.Children.Count == 0)
            W("❌ 根节点 Children 为空 —— 左侧树展开后没有任何分区/目录。");
        else
            W($"✅ 根节点已加载 {root.Children.Count} 个子项。");
        W("");

        // 模拟交互 1：点击左侧树的某个分区/目录节点 → 右侧列表应加载其内容
        foreach (var child in root.Children)
        {
            int before = filesNotify;
            vm.SelectedFolder = child;
            W($"[点击树节点] '{child.Name}'  → 右侧列表 {vm.Files.Count} 项（通知 {filesNotify - before} 次）");

            // 模拟交互 2：双击右侧列表里的目录项 → 应进入该目录
            var firstDir = vm.Files.FirstOrDefault(f => f.SizeText == "<目录>");
            if (firstDir != null)
            {
                int before2 = filesNotify;
                vm.OpenItem(firstDir);
                W($"   [双击进入] '{firstDir.Name}'  → 右侧列表 {vm.Files.Count} 项（通知 {filesNotify - before2} 次），面包屑='{vm.CurrentPath}'");
                var sample = vm.Files.Take(6).Select(f => f.Name).ToList();
                if (sample.Count > 0)
                    W($"      内容: {string.Join(", ", sample)}");
                W($"   面包屑层级数: {vm.Breadcrumbs.Count}  路径: {vm.CurrentPathText}");
            }
            else
            {
                W("   （该层没有子目录项）");
            }
            break; // 只测第一个子项即可
        }

        W("");
        W(filesNotify > 0
            ? "✅ 右侧列表在整条交互链中始终收到 CollectionChanged —— 无需调整窗口大小即可显示。"
            : "⚠ 右侧列表从未收到通知（若仍能显示，说明是全量重绘而非通知驱动）。");

        vm.UnmountOnExit();
        return sb.ToString();
    }
}
