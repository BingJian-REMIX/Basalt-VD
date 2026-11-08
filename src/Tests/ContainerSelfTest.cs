using System.IO;
using System.Text;
using Basalt.Models;
using Basalt.Services;

namespace Basalt.Services;

/// <summary>
/// 容器（zip/iso/rar/7z/SFX）专项自测：
/// 格式识别 → 列出条目 → 目录树 → 提取落盘 → 元数据 → 图标。
/// </summary>
public static class ContainerSelfTest
{
    public static string Run(string path)
    {
        var sb = new StringBuilder();
        void W(string s) => sb.AppendLine(s);

        W("=== Basalt 容器自测 ===");
        W($"目标: {path}");
        W("");

        // ---- 1. 格式识别 ----
        W("---- 格式识别（魔数）----");
        var fmt = ContainerDetector.DetectFile(path);
        W($"   检测结果: {fmt} ({ContainerDetector.DisplayName(fmt)})");
        W($"   7-Zip 路径: {ContainerDetector.SevenZipPath ?? "未找到"}");
        W($"   IsContainerFile: {ContainerDetector.IsContainerFile(path)}");
        if (fmt == ContainerFormat.None)
        {
            W("   ❌ 未识别为容器文件");
            return sb.ToString();
        }
        W("");

        // ---- 2. 建立容器引擎并列出内容 ----
        ContainerDiskService? disk = null;
        try
        {
            disk = new ContainerDiskService(path);
            W("---- 归档内容 ----");
            W($"   根目录子项 {disk.GetDirectories("").Count} 个，直接文件 {disk.GetFiles("").Count} 个");

            // 递归列出前若干条
            var all = new List<string>();
            void Walk(string dir, int depth)
            {
                if (depth > 3) return;
                foreach (var d in disk.GetDirectories(dir))
                {
                    all.Add($"[D] {d.Name}");
                    Walk(d.FullPath, depth + 1);
                }
                foreach (var f in disk.GetFiles(dir))
                    all.Add($"[F] {f.Name}  {f.SizeText}  {f.ModifiedText}");
            }
            Walk("", 0);
            foreach (var line in all.Take(25)) W($"   {line}");
            if (all.Count > 25) W($"   … 共 {all.Count} 项");
            W("");

            // ---- 3. 面包屑 ----
            var deep = all.Count > 0 ? FindDeepestDir(disk) : null;
            if (deep != null)
            {
                var crumbs = disk.BuildBreadcrumb(deep);
                W($"---- 面包屑（{deep}）----");
                W("   " + string.Join(" > ", crumbs.Select(c => c.Display)));
            }
            W("");

            // ---- 4. 元数据 + 图标 ----
            W("---- 元数据与图标 ----");
            var firstFile = disk.GetFiles(deep ?? "").FirstOrDefault();
            if (firstFile != null)
            {
                var meta = disk.GetMetadata(firstFile.FullPath);
                W($"   {meta.Name}  大小={meta.SizeText}  修改={meta.ModifiedText}  属性={meta.AttributesText}");
                W($"   图标: {(firstFile.IconSource != null ? "已取到" : "无")}");
            }
            else W("   （该层无文件）");
            W("");

            // ---- 5. 提取落盘 + 内容比对 ----
            W("---- 提取落盘 ----");
            var anyFile = FindFirstFile(disk, "");
            if (anyFile != null)
            {
                string dest = Path.Combine(Path.GetTempPath(), $"basalt_ct_{Guid.NewGuid():N}{Path.GetExtension(anyFile.Name)}");
                try
                {
                    disk.ExtractFile(anyFile.FullPath, dest);
                    bool ok = File.Exists(dest);
                    long len = ok ? new FileInfo(dest).Length : 0;
                    W($"   ✅ 提取 '{anyFile.Name}' → {len} 字节（归档内标记 {anyFile.SizeText}）");
                    if (ok)
                    {
                        // 内容比对
                        string head = File.ReadAllText(dest)[..Math.Min(40, (int)Math.Min(len, 40))].Replace("\r", "").Replace("\n", "\\n");
                        W($"   内容开头: {head}");
                    }
                    try { if (ok) File.Delete(dest); } catch { }
                }
                catch (Exception ex)
                {
                    W($"   ❌ 提取失败: {ex.GetType().Name}: {ex.Message}");
                }
            }
            else W("   （归档内无可提取文件）");
        }
        catch (Exception ex)
        {
            W($"❌ 容器引擎异常: {ex.GetType().Name}: {ex.Message}");
            W(ex.StackTrace ?? "");
        }
        finally
        {
            disk?.Dispose();
        }

        return sb.ToString();
    }

    private static string? FindDeepestDir(ContainerDiskService disk)
    {
        string? best = null;
        int bestDepth = -1;
        void Walk(string dir, int depth)
        {
            if (depth > 3) return;
            foreach (var d in disk.GetDirectories(dir))
            {
                if (depth > bestDepth) { bestDepth = depth; best = d.FullPath; }
                Walk(d.FullPath, depth + 1);
            }
        }
        Walk("", 0);
        return best;
    }

    private static FileItem? FindFirstFile(ContainerDiskService disk, string dir)
    {
        var f = disk.GetFiles(dir).FirstOrDefault();
        if (f != null) return f;
        foreach (var d in disk.GetDirectories(dir))
        {
            var r = FindFirstFile(disk, d.FullPath);
            if (r != null) return r;
        }
        return null;
    }
}
