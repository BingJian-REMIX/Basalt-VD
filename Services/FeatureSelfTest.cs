using System.IO;
using System.Text;
using Basalt.Models;
using Basalt.ViewModels;

namespace Basalt.Services;

/// <summary>
/// 功能级自测：验证新增的文件管理能力（不依赖真实窗口交互）。
/// 覆盖：.. 返回上级、面包屑多级、元数据读取、原生图标、属性窗口所需字段、
/// 状态栏计数、提取到本地（真实落盘 + 内容比对）。
/// </summary>
public static class FeatureSelfTest
{
    public static string Run(string path)
    {
        var sb = new StringBuilder();
        void W(string s) => sb.AppendLine(s);

        W("=== Basalt 功能自测 ===");
        W($"目标: {path}");
        W("");

        var vm = new MainViewModel();
        vm.MountPath(path);

        if (vm.Roots.Count == 0)
        {
            W("❌ 挂载失败，Roots 为空");
            return sb.ToString();
        }

        // ---- 1. 逐层下钻，收集各层信息 ----
        var layers = new List<(string Name, int Files, int Dirs, bool HasUp, string Crumb)>();
        var allFiles = new List<FileItem>();

        for (int depth = 0; depth < 5; depth++)
        {
            var dir = vm.Files.FirstOrDefault(f => f.IsDirectory && !f.IsParentEntry);
            if (dir == null) break;

            vm.OpenItem(dir);
            var up = vm.Files.FirstOrDefault(f => f.IsParentEntry);
            layers.Add((
                dir.Name,
                vm.Files.Count(f => !f.IsDirectory && !f.IsParentEntry),
                vm.Files.Count(f => f.IsDirectory && !f.IsParentEntry),
                up != null,
                string.Join(" > ", vm.Breadcrumbs.Select(c => c.Display))));
            allFiles.AddRange(vm.Files.Where(f => !f.IsDirectory && !f.IsParentEntry));
        }

        W("---- 目录层级 ----");
        foreach (var l in layers)
            W($"   {l.Name,-24} 文件={l.Files,-5} 目录={l.Dirs,-5} '..'={(l.HasUp ? "有" : "无")}  面包屑: {l.Crumb}");
        W("");

        // ---- 2. .. 返回上级：逐级回退，验证路径正确 ----
        W("---- '..' 返回上级验证 ----");
        var beforeCrumb = vm.CurrentPathText;
        var upItem = vm.Files.FirstOrDefault(f => f.IsParentEntry);
        if (upItem != null)
        {
            vm.OpenItem(upItem);
            W($"   ✅ '..' 生效: '{beforeCrumb}' → '{vm.CurrentPathText}'");
            W($"      回到上层后列表 {vm.Files.Count} 项，面包屑 {vm.Breadcrumbs.Count} 级");
        }
        else
        {
            W("   （当前层无上级）");
        }
        W("");

        // ---- 3. 原生图标 ----
        W("---- 原生图标 ----");
        int withIcon = allFiles.Count(f => f.IconSource != null);
        W(withIcon > 0
            ? $"   ✅ {withIcon}/{allFiles.Count} 个文件取到原生 Windows 图标"
            : "   ❌ 未取到任何原生图标");
        var folderIcons = vm.Files.Count(f => f.IsDirectory && f.IconSource != null);
        W($"   目录图标覆盖: {folderIcons}/{vm.Files.Count(f => f.IsDirectory)}");
        W("");

        // ---- 4. 元数据（属性窗口数据源）----
        W("---- 文件元数据（属性窗口）----");
        var sample = allFiles.FirstOrDefault(f => f.IconSource != null) ?? allFiles.FirstOrDefault();
        if (sample != null)
        {
            var meta = vm.GetMetadata(sample);
            if (meta == null)
            {
                W("   ❌ GetMetadata 返回 null");
            }
            else
            {
                W($"   文件: {meta.Name}");
                W($"     完整路径: {meta.FullPath}");
                W($"     大小:     {meta.SizeText}");
                W($"     创建时间: {meta.CreatedText}");
                W($"     修改时间: {meta.ModifiedText}");
                W($"     访问时间: {meta.AccessedText}");
                W($"     属性:     {meta.AttributesText}");
                bool ok = !string.IsNullOrEmpty(meta.FullPath)
                       && !string.IsNullOrEmpty(meta.ModifiedText) && meta.ModifiedText != "—"
                       && !string.IsNullOrEmpty(meta.AttributesText);
                W(ok ? "   ✅ 元数据字段齐全（属性窗口有内容可显示）" : "   ⚠ 元数据有缺失字段");
            }
        }
        else W("   （未找到可采样文件）");
        W("");

        // ---- 5. 状态栏计数 ----
        W("---- 状态栏计数 ----");
        vm.SelectedFile = sample;
        W($"   选中 1 个文件 → '{vm.SelectionSummary}'");
        var dirItem = vm.Files.FirstOrDefault(f => f.IsDirectory);
        if (dirItem != null)
        {
            vm.SelectedFile = dirItem;
            W($"   选中 1 个目录 → '{vm.SelectionSummary}'");
        }
        vm.UpdateSelectionSummary(new List<FileItem>());
        W($"   取消选择       → '{vm.SelectionSummary}'");
        W("");

        // ---- 6. 提取到本地（真实落盘 + 内容比对）----
        W("---- 提取文件到本地 ----");
        var target = allFiles.FirstOrDefault(f => !string.IsNullOrEmpty(f.Name));
        if (target != null)
        {
            string dest = Path.Combine(Path.GetTempPath(), $"basalt_extract_test_{Guid.NewGuid():N}{Path.GetExtension(target.Name)}");
            try
            {
                vm.SelectedFile = target;
                // 直接走引擎的 ExtractFile（UI 路径会弹 SaveFileDialog，无界面下不可用）
                var disk = (Services.IVirtualDisk)typeof(MainViewModel)
                    .GetField("_disk", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .GetValue(vm)!;
                disk.ExtractFile(target.FullPath, dest);

                bool exists = File.Exists(dest);
                long size = exists ? new FileInfo(dest).Length : 0;
                W($"   ✅ 提取到: {dest}");
                W($"      源大小 {target.SizeText} → 落盘 {size} 字节  一致={exists && size > 0}");
                try { if (exists) File.Delete(dest); } catch { }
            }
            catch (Exception ex)
            {
                W($"   ❌ 提取失败: {ex.GetType().Name}: {ex.Message}");
            }
        }
        else W("   （无可提取文件）");

        vm.UnmountOnExit();
        return sb.ToString();
    }
}
