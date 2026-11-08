using System.IO;

namespace Basalt.Services;

public enum ExtractPhase
{
    Scanning,   // 目录树扫描中（Loading / 不确定进度）
    Copying,    // 流式复制中（确定进度）
    Done,
    Failed
}

/// <summary>递归提取进度快照。</summary>
public sealed class ExtractProgress
{
    public ExtractPhase Phase { get; init; }
    public string Message { get; init; } = "";
    public int FilesDone { get; init; }
    public int FilesTotal { get; init; }
    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }
}

/// <summary>递归提取结果。</summary>
public sealed class ExtractResult
{
    public int Files { get; set; }
    public int Directories { get; set; }
    public int Skipped { get; set; }
    public long Bytes { get; set; }
    public bool Cancelled { get; set; }
    public List<string> Errors { get; } = new();

    public string Summary =>
        (Cancelled ? "已取消：" : "") +
        $"重建 {Directories} 个目录、写入 {Files} 个文件 / {Models.FileItemFormatter.FormatSize(Bytes)}" +
        (Skipped > 0 ? $"，跳过 {Skipped} 项" : "");
}

/// <summary>
/// 把镜像内的文件夹递归提取到本地：通过 IVirtualDisk.GetDirectories / GetFiles 遍历，
/// 用 IVirtualDisk.OpenRead 的只读流逐个写到本地，重建完全相同的目录结构。
/// 三引擎通用（VHD 真实路径 / VMDK DiscUtils 流 / 容器 7z 内存流）。
/// </summary>
public static class RecursiveExtractor
{
    private const int MaxDepth = 128;
    private const int BufferSize = 128 * 1024;

    private sealed record DirEntry(string Source, string Relative);
    private sealed record FileEntry(string Source, string Relative, long Size);

    /// <summary>
    /// 在 destRoot 下创建与源文件夹同名的子目录，并把整棵子树写进去。
    /// </summary>
    public static ExtractResult Extract(IVirtualDisk disk, string sourceDir, string destRoot,
        IProgress<ExtractProgress>? progress = null, CancellationToken token = default)
    {
        var result = new ExtractResult();
        progress?.Report(new ExtractProgress { Phase = ExtractPhase.Scanning, Message = "正在扫描目录树..." });

        try
        {
            string dest = Path.GetFullPath(destRoot);
            Directory.CreateDirectory(dest);
            string targetRoot = Path.Combine(dest, SafeSegment(FolderName(sourceDir)));
            Directory.CreateDirectory(targetRoot);

            var dirs = Collect(disk, sourceDir, token, result);

            var files = dirs.Files;
            long totalBytes = files.Sum(f => f.Size);
            progress?.Report(new ExtractProgress
            {
                Phase = ExtractPhase.Copying,
                Message = $"共 {dirs.Dirs.Count} 个目录 / {files.Count} 个文件",
                FilesTotal = files.Count,
                BytesTotal = totalBytes
            });

            // 先建目录，保证空目录也被还原
            foreach (var dir in dirs.Dirs)
            {
                token.ThrowIfCancellationRequested();
                try { Directory.CreateDirectory(ResolveSafe(targetRoot, dir.Relative)); result.Directories++; }
                catch (Exception ex) { result.Skipped++; Record(result, dir.Source, ex); }
            }

            // 再按 OpenRead 流写文件
            int done = 0;
            long bytes = 0;
            foreach (var file in files)
            {
                if (token.IsCancellationRequested) { result.Cancelled = true; break; }
                string local = ResolveSafe(targetRoot, file.Relative);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                    using var src = disk.OpenRead(file.Source);
                    using var dst = new FileStream(local, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize);
                    src.CopyTo(dst, BufferSize);
                    result.Files++;
                    result.Bytes += dst.Length;
                    bytes += dst.Length;
                }
                catch (Exception ex)
                {
                    result.Skipped++;
                    Record(result, file.Source, ex);
                }

                done++;
                // 节流上报，避免海量小文件刷爆 UI 线程
                if (done % 8 == 0 || done == files.Count)
                {
                    progress?.Report(new ExtractProgress
                    {
                        Phase = ExtractPhase.Copying,
                        Message = Path.GetFileName(local),
                        FilesDone = done,
                        FilesTotal = files.Count,
                        BytesDone = bytes,
                        BytesTotal = totalBytes
                    });
                }
            }

            progress?.Report(new ExtractProgress
            {
                Phase = ExtractPhase.Done,
                Message = result.Summary,
                FilesDone = done,
                FilesTotal = files.Count,
                BytesDone = bytes,
                BytesTotal = totalBytes
            });
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
            progress?.Report(new ExtractProgress { Phase = ExtractPhase.Done, Message = "已取消" });
        }
        catch (Exception ex)
        {
            Record(result, destRoot, ex);
            progress?.Report(new ExtractProgress { Phase = ExtractPhase.Failed, Message = ex.Message });
        }
        return result;
    }

    /// <summary>递归遍历目录树，收集目录与文件（相对路径）。</summary>
    private static (List<DirEntry> Dirs, List<FileEntry> Files) Collect(
        IVirtualDisk disk, string sourceDir, CancellationToken token, ExtractResult result)
    {
        var dirs = new List<DirEntry>();
        var files = new List<FileEntry>();
        var stack = new Stack<DirEntry>();
        stack.Push(new DirEntry(sourceDir, ""));

        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            var segments = dir.Relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= MaxDepth) { result.Skipped++; continue; }
            dirs.Add(dir);

            try
            {
                foreach (var f in disk.GetFiles(dir.Source))
                {
                    string rel = Join(dir.Relative, f.Name);
                    if (!IsSafeRelative(rel)) { result.Skipped++; continue; }
                    files.Add(new FileEntry(f.FullPath, rel, f.Size));
                }
            }
            catch (Exception ex) { Record(result, dir.Source, ex); result.Skipped++; }

            try
            {
                foreach (var sub in disk.GetDirectories(dir.Source))
                {
                    string rel = Join(dir.Relative, sub.Name);
                    if (!IsSafeRelative(rel)) { result.Skipped++; continue; }
                    stack.Push(new DirEntry(sub.FullPath, rel));
                }
            }
            catch (Exception ex) { Record(result, dir.Source, ex); result.Skipped++; }
        }

        dirs.Sort((a, b) => string.CompareOrdinal(a.Relative, b.Relative));
        return (dirs, files);
    }

    private static string Join(string parent, string name)
        => parent.Length == 0 ? name : parent + "\\" + name;

    private static string FolderName(string sourceDir)
    {
        string name = sourceDir.Split('\\', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        if (name.Length == 0 || int.TryParse(name, out _)) name = "partition_root";
        return SafeSegment(name);
    }

    /// <summary>zip-slip 防护：拒绝 ".."、绝对路径、非法字符。</summary>
    private static bool IsSafeRelative(string relative)
    {
        if (relative.StartsWith('\\') || relative.Contains(':') || relative.Contains("..")) return false;
        foreach (char c in Path.GetInvalidPathChars())
            if (relative.Contains(c)) return false;
        foreach (string seg in relative.Split('\\'))
            if (seg.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
        return true;
    }

    private static string SafeSegment(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "folder" : name.Trim();
    }

    /// <summary>把相对路径解析成目标根下的绝对路径，并校验没有越界。</summary>
    private static string ResolveSafe(string root, string relative)
    {
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', '\\')));
        string prefix = root.TrimEnd('\\') + '\\';
        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"路径越界，已拒绝：{relative}");
        return full;
    }

    private static void Record(ExtractResult result, string path, Exception ex)
    {
        if (result.Errors.Count >= 20) return;
        result.Errors.Add($"{path} — {ex.GetType().Name}: {ex.Message}");
    }
}
