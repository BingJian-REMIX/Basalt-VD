using System.IO;

namespace Basalt.Common;

/// <summary>
/// 外部打开兜底用的临时文件登记处。
/// VMDK / 容器内的文件不在真实文件系统上，交给系统默认程序前必须先落地；
/// 登记后由 App.OnExit 统一清理，避免 Temp 目录无限堆积。
/// </summary>
public static class TempFileRegistry
{
    public static string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "BasaltOpen");

    private static readonly object Gate = new();
    private static readonly HashSet<string> Tracked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>生成一个带随机前缀、保留原扩展名的临时文件路径并登记。</summary>
    public static string NewPath(string fileName)
    {
        Directory.CreateDirectory(DirectoryPath);
        string safe = Sanitize(fileName);
        string path = Path.Combine(DirectoryPath, $"{Guid.NewGuid():N}_{safe}");
        Track(path);
        return path;
    }

    /// <summary>登记一个已经写好的临时文件。</summary>
    public static void Track(string path)
    {
        lock (Gate) Tracked.Add(path);
    }

    /// <summary>退出时删除本次会话产生的临时文件；被外部程序占用时静默跳过。</summary>
    public static void Cleanup()
    {
        string[] paths;
        lock (Gate) { paths = Tracked.ToArray(); Tracked.Clear(); }

        foreach (var p in paths)
        {
            try { if (File.Exists(p)) File.Delete(p); } catch { /* 外部程序仍占用，留给系统清理 */ }
        }

        try
        {
            if (Directory.Exists(DirectoryPath) && !Directory.EnumerateFileSystemEntries(DirectoryPath).Any())
                Directory.Delete(DirectoryPath);
        }
        catch { /* 目录非空或权限不足 */ }
    }

    private static string Sanitize(string fileName)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "preview.bin";
        return fileName;
    }
}
