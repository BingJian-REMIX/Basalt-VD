using System.IO;
using Basalt.Models;

namespace Basalt.Services;

/// <summary>深度清理扫描到的一个目标目录。</summary>
public sealed class CleanupTarget
{
    public string Display { get; init; } = "";
    public string Path { get; init; } = "";      // 引擎内路径（VHD 即真实路径）
    public string RootPath { get; init; } = "";  // 所属盘符/分区根，删除时用来防越界
    public long Bytes { get; init; }
    public int FileCount { get; init; }
    public int InaccessibleCount { get; init; }
    public bool IsSelected { get; set; } = true;

    public string SizeText => FileItemFormatter.FormatSize(Bytes);
    public string Note => FileCount == 0
        ? "空目录"
        : $"{FileCount} 个文件" + (InaccessibleCount > 0 ? $" · {InaccessibleCount} 处无权限/读取失败" : "");
}

/// <summary>删除结果汇总。</summary>
public sealed class CleanupResult
{
    public int DeletedFiles { get; set; }
    public int SkippedFiles { get; set; }
    public long FreedBytes { get; set; }
    public List<string> Errors { get; } = new();

    public string Summary =>
        $"已删除 {DeletedFiles} 个文件，释放 {FileItemFormatter.FormatSize(FreedBytes)}" +
        (SkippedFiles > 0 ? $"；跳过 {SkippedFiles} 个（占用中 / 权限不足 / 链接）" : "") +
        (Errors.Count > 0 ? $"\n\n前 {Math.Min(3, Errors.Count)} 条原因：\n" +
            string.Join("\n", Errors.Take(3)) : "");
}

/// <summary>
/// 深度清理：扫描挂载盘 / 分区根下的回收站、系统临时目录、系统更新缓存，
/// 递归统计大小；删除仅在 VHD 可写挂载时可用。
/// <para>扫描走 IVirtualDisk 抽象，三引擎通用；删除走真实文件系统，
/// 逐文件捕获权限异常并跳过 reparse point，绝不改动 VMDK / 归档原始镜像。</para>
/// </summary>
public static class CleanupService
{
    private const int MaxDepth = 64;

    private static readonly (string Dir, string Display)[] RootTargets =
    {
        ("$Recycle.Bin", "回收站（$Recycle.Bin）")
    };

    /// <summary>当前引擎是否允许删除。VMDK 以只读打开、容器是只读归档，都不能写回。</summary>
    public static bool CanDelete(IVirtualDisk disk)
        => disk is VhdDiskService { MountedReadOnly: false };

    /// <summary>不允许删除时给用户的说明。</summary>
    public static string DeleteBlockedReason(IVirtualDisk disk) => disk switch
    {
        VhdDiskService { MountedReadOnly: true } => "当前 VHD 以只读方式挂载，无法清理。",
        VmdkDiskService => "VMDK 为内存只读解析，清理只会扫描统计，不会写回原始镜像。",
        ContainerDiskService => "归档 / ISO 为只读浏览，清理只会扫描统计，不修改原始文件。",
        _ => "当前磁盘不支持写入。"
    };

    /// <summary>扫描所有目标目录，返回按大小降序的列表。</summary>
    public static List<CleanupTarget> Scan(IVirtualDisk disk)
    {
        var result = new List<CleanupTarget>();
        foreach (string root in GetRoots(disk))
        {
            foreach (var (dir, display) in RootTargets)
                AddIfFound(disk, root, FindChild(disk, root, dir), display, result);

            string? windows = FindChild(disk, root, "Windows");
            if (windows == null) continue;

            AddIfFound(disk, root, FindChild(disk, windows, "Temp"),
                "系统临时文件（Windows\\Temp）", result);

            string? softwareDist = FindChild(disk, windows, "SoftwareDistribution");
            if (softwareDist != null)
                AddIfFound(disk, root, FindChild(disk, softwareDist, "Download"),
                    "系统更新缓存（SoftwareDistribution\\Download）", result);
        }
        result.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
        return result;
    }

    private static IEnumerable<string> GetRoots(IVirtualDisk disk)
    {
        if (disk.DriveLetter is char letter)
        {
            yield return $"{letter}:\\";
            yield break;
        }
        if (disk is VmdkDiskService)
        {
            // 镜像根下列出的是各分区（伪路径 "0"、"1" …）
            foreach (var p in disk.GetDirectories(""))
                yield return p.FullPath;
            yield break;
        }
        yield return "";   // 容器：归档根
    }

    /// <summary>在父目录下按名字找直接子目录；访问被拒时返回 null 而不是让整个扫描中断。</summary>
    private static string? FindChild(IVirtualDisk disk, string parent, string name)
    {
        try
        {
            return disk.GetDirectories(parent)
                .FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
                ?.FullPath;
        }
        catch
        {
            return null;
        }
    }

    private static void AddIfFound(IVirtualDisk disk, string root, string? path, string display,
        List<CleanupTarget> result)
    {
        if (path == null) return;
        var stat = new Stat();
        Walk(disk, path, 0, stat);
        result.Add(new CleanupTarget
        {
            Display = display,
            Path = path,
            RootPath = root,
            Bytes = stat.Bytes,
            FileCount = stat.Files,
            InaccessibleCount = stat.Inaccessible
        });
    }

    private sealed class Stat
    {
        public long Bytes;
        public int Files;
        public int Inaccessible;
    }

    /// <summary>递归统计大小；每一层单独捕获异常，单个无权限目录不影响其它目录。</summary>
    private static void Walk(IVirtualDisk disk, string path, int depth, Stat stat)
    {
        if (depth > MaxDepth) { stat.Inaccessible++; return; }

        try
        {
            foreach (var file in disk.GetFiles(path))
            {
                stat.Bytes += file.Size;
                stat.Files++;
            }
        }
        catch { stat.Inaccessible++; }

        List<FolderNode> dirs;
        try { dirs = disk.GetDirectories(path); }
        catch { stat.Inaccessible++; return; }

        foreach (var dir in dirs)
            Walk(disk, dir.FullPath, depth + 1, stat);
    }

    /// <summary>删除入口：先校验引擎可写，再按真实路径删除。</summary>
    public static CleanupResult Delete(IVirtualDisk disk, IEnumerable<CleanupTarget> targets)
    {
        if (!CanDelete(disk))
            throw new InvalidOperationException(DeleteBlockedReason(disk));
        return DeleteTargets(targets);
    }

    /// <summary>
    /// 真实文件系统删除。默认只删内容、保留目标目录本身；
    /// 跳过 reparse point（junction / 符号链接）防止删出盘符范围。
    /// <para>
    /// <paramref name="removeTargetRoot"/> 供 ISO 精简复用：删除整个目标目录（含空壳），
    /// 避免镜像里残留空的 Defender / UWP 应用目录。
    /// </para>
    /// </summary>
    public static CleanupResult DeleteTargets(IEnumerable<CleanupTarget> targets,
        bool removeTargetRoot = false, bool clearReadOnly = false)
    {
        var result = new CleanupResult();
        foreach (var target in targets)
        {
            string root = Path.GetFullPath(target.RootPath);
            string full = Path.GetFullPath(target.Path);
            // 防误杀：目标必须位于所属盘符根之下，且不是盘符根本身
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(full.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                result.Errors.Add($"越界路径已拒绝：{full}");
                continue;
            }
            if (!Directory.Exists(full)) continue;
            DeleteTree(full, full, result, 0, removeTargetRoot, clearReadOnly);
            if (removeTargetRoot)
            {
                if (clearReadOnly) ClearDirReadOnly(full);
                try { Directory.Delete(full, recursive: false); } catch { /* 非空或无权限，保留 */ }
            }
        }
        return result;
    }

    private static void DeleteTree(string dir, string targetRoot, CleanupResult result, int depth,
        bool removeTargetRoot = false, bool clearReadOnly = false)
    {
        if (depth > MaxDepth) return;

        string[] children;
        try { children = Directory.GetDirectories(dir); }
        catch (Exception ex) { Record(result, dir, ex); return; }

        foreach (string child in children)
        {
            try
            {
                // 符号链接 / junction 可能指向盘符之外，直接跳过整棵子树
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                {
                    result.Errors.Add($"已跳过链接目录：{child}");
                    continue;
                }
            }
            catch (Exception ex) { Record(result, child, ex); continue; }

            DeleteTree(child, targetRoot, result, depth + 1, removeTargetRoot, clearReadOnly);
        }

        string[] files;
        try { files = Directory.GetFiles(dir); }
        catch (Exception ex) { Record(result, dir, ex); return; }

        foreach (string file in files)
        {
            try
            {
                long size = new FileInfo(file).Length;
                // 默认不主动改属性（VHD 深度清理保持谨慎）；ISO 精简作用于临时副本，
                // 明确允许清除 ReadOnly，否则 wimlib apply 出来的系统文件会大量跳过。
                if (clearReadOnly) ClearFileReadOnly(file);
                File.Delete(file);
                result.DeletedFiles++;
                result.FreedBytes += size;
            }
            catch (UnauthorizedAccessException ex) { result.SkippedFiles++; Record(result, file, ex); }
            catch (IOException ex) { result.SkippedFiles++; Record(result, file, ex); }
            catch (Exception ex) { result.SkippedFiles++; Record(result, file, ex); }
        }

        // 自底向上删空子目录，保留目标目录本身
        if (!string.Equals(dir, targetRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (clearReadOnly) ClearDirReadOnly(dir);
            try { Directory.Delete(dir, recursive: false); } catch { /* 非空或无权限，保留 */ }
        }
    }

    private static void ClearFileReadOnly(string path)
    {
        try
        {
            var attr = File.GetAttributes(path);
            if ((attr & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);
        }
        catch { /* 交给 File.Delete 的异常分支记录 */ }
    }

    private static void ClearDirReadOnly(string path)
    {
        try
        {
            var attr = File.GetAttributes(path);
            if ((attr & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attr & ~FileAttributes.ReadOnly);
        }
        catch { /* 交给 Directory.Delete 的异常分支 */ }
    }

    private static void Record(CleanupResult result, string path, Exception ex)
    {
        if (result.Errors.Count >= 20) return;   // 避免海量错误撑爆界面
        result.Errors.Add($"{path} — {ex.GetType().Name}: {ex.Message}");
    }
}
