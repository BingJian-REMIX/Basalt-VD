using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Basalt.Common;
using Basalt.Models;
using Basalt.ViewModels;

namespace Basalt.Services;

/// <summary>
/// 容器虚拟磁盘：把 zip / iso / rar / 7z / SFX 等归档当作一个"磁盘"来浏览。
/// <para>
/// 后端用 7-Zip 的 CLI（<c>7z l -slt</c> 列目录、<c>7z e</c> 提取）——
/// 它能识别 60+ 种格式，比自己实现各格式解析器现实得多。
/// </para>
/// <para>
/// 伪路径约定：<c>""</c> = 归档根，<c>"sub\dir"</c> = 归档内目录，<c>"sub\file.txt"</c> = 归档内文件。
/// 目录列表由 7z 一次性列出全量条目后按前缀分组建树（归档内条目数通常不大，无需惰性）。
/// </para>
/// </summary>
public sealed class ContainerDiskService : IVirtualDisk
{
    private readonly string _archivePath;
    private readonly List<Entry> _entries = new();

    /// <summary>归档内的一个条目（文件或目录）。</summary>
    private sealed class Entry
    {
        public string Path = "";       // 归档内相对路径，统一用 '\' 分隔
        public bool IsDirectory;
        public long Size;
        public DateTime Modified;
        public string Attributes = "";
    }

    public string DisplayName { get; }
    public char? DriveLetter => null;
    public ContainerFormat Format { get; }

    public ContainerDiskService(string archivePath)
    {
        _archivePath = archivePath;
        DisplayName = Path.GetFileName(archivePath);
        Format = ContainerDetector.DetectFile(archivePath);
        LoadEntries();
    }

    /// <summary>解析 7z 的 -slt 结构化输出。</summary>
    private void LoadEntries()
    {
        string? sevenZip = ContainerDetector.SevenZipPath;
        if (sevenZip == null)
            throw new InvalidOperationException("未找到 7-Zip（7z.exe），无法浏览归档内容。请安装 7-Zip 后重试。");

        var psi = new ProcessStartInfo(sevenZip)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("l");
        psi.ArgumentList.Add("-slt");
        psi.ArgumentList.Add("-ba");          // 不带表头，纯条目
        psi.ArgumentList.Add(_archivePath);

        using var proc = Process.Start(psi);
        if (proc == null) throw new InvalidOperationException("无法启动 7-Zip。");

        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(60000);

        if (proc.ExitCode != 0)
            throw new InvalidDataException($"7-Zip 无法读取该归档：{stderr.Trim()}");

        ParseEntries(stdout);
    }

    private void ParseEntries(string slt)
    {
        var path = new StringBuilder();
        string? folder = null, size = null, modified = null, attributes = null;

        void Flush()
        {
            if (path.Length == 0) return;
            string p = path.ToString().Replace('/', '\\').TrimStart('\\');
            if (p.Length == 0) { path.Clear(); return; }

            bool isDir = folder == "+";
            // 目录条目（Folder = +）没有 Size
            var e = new Entry
            {
                Path = p,
                IsDirectory = isDir,
                Size = long.TryParse(size, out var s) ? s : 0,
                Modified = DateTime.TryParse(modified, out var m) ? m : default,
                Attributes = attributes ?? ""
            };
            _entries.Add(e);

            path.Clear(); folder = null; size = null; modified = null; attributes = null;
        }

        foreach (var rawLine in slt.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0) { Flush(); continue; }

            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim();
            string val = line[(eq + 1)..].Trim();

            switch (key)
            {
                case "Path": path.Clear(); path.Append(val); break;
                case "Folder": folder = val; break;
                case "Size": size = val; break;
                case "Modified": modified = val; break;
                case "Attributes": attributes = val; break;
            }
        }
        Flush();
    }

    /// <summary>
    /// 列出某目录下的直接子项。
    /// <para>
    /// 7z 给的是全路径（如 "src\sub\a.txt"）。判定"直接子项"的方法：
    /// 去掉目录前缀后，剩余部分不应再含分隔符。
    /// 另外 7z 的目录条目自带 "Folder = +"，无需靠路径推断；
    /// 但有些归档（尤其 SFX 内层）不给目录条目，需要按路径补齐 —— 见 GetDirectories。
    /// </para>
    /// </summary>
    private IEnumerable<Entry> ChildrenOf(string dirPath)
    {
        string prefix = dirPath.Length == 0 ? "" : dirPath + "\\";
        foreach (var e in _entries)
        {
            if (!e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            string rest = e.Path[prefix.Length..];
            if (rest.Length == 0) continue;
            if (rest.Contains('\\')) continue;   // 更深层，不在当前层
            yield return e;
        }
    }

    /// <summary>取得某目录的直接子目录名集合（含从文件路径推断出的隐含目录）。</summary>
    private HashSet<string> DirectChildDirs(string dirPath)
    {
        string prefix = dirPath.Length == 0 ? "" : dirPath + "\\";
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) 归档里显式声明的目录条目
        foreach (var e in _entries)
        {
            if (!e.IsDirectory) continue;
            if (!e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            string rest = e.Path[prefix.Length..];
            if (rest.Length == 0 || rest.Contains('\\')) continue;
            dirs.Add(e.Path);
        }

        // 2) 从文件路径推断：有些归档不给目录条目，但文件路径里带着层级
        foreach (var e in _entries)
        {
            if (!e.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            string rest = e.Path[prefix.Length..];
            int sep = rest.IndexOf('\\');
            if (sep < 0) continue;              // 直接子文件，不产生目录
            dirs.Add(prefix + rest[..sep]);
        }

        return dirs;
    }

    public List<FolderNode> GetDirectories(string path)
    {
        var list = new List<FolderNode>();
        foreach (var dirPath in DirectChildDirs(path))
        {
            list.Add(new FolderNode
            {
                Name = NameOf(dirPath),
                FullPath = dirPath,
                IconSource = IconProvider.FolderIcon
            });
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    public List<FileItem> GetFiles(string path)
    {
        var list = new List<FileItem>();
        if (path.Length == 0)
        {
            // 归档根：直接子文件（不含任何子目录里的）
            foreach (var e in _entries.Where(x => !x.IsDirectory && !x.Path.Contains('\\')))
            {
                string ext = Path.GetExtension(e.Path);
                list.Add(new FileItem
                {
                    Name = NameOf(e.Path),
                    FullPath = e.Path,
                    SizeText = FileItemFormatter.FormatSize(e.Size),
                    ModifiedText = FileItemFormatter.FormatTime(e.Modified),
                    CreatedText = "",
                    AttributesText = e.Attributes,
                    IconSource = IconProvider.FromExtension(ext),
                    IsDirectory = false
                });
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return list;
        }

        foreach (var e in ChildrenOf(path).Where(x => !x.IsDirectory))
        {
            string ext = Path.GetExtension(e.Path);
            list.Add(new FileItem
            {
                Name = NameOf(e.Path),
                FullPath = e.Path,
                SizeText = FileItemFormatter.FormatSize(e.Size),
                ModifiedText = FileItemFormatter.FormatTime(e.Modified),
                CreatedText = "",
                AttributesText = e.Attributes,
                IconSource = IconProvider.FromExtension(ext),
                IsDirectory = false
            });
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    private static string NameOf(string path)
    {
        int sep = path.LastIndexOf('\\');
        return sep < 0 ? path : path[(sep + 1)..];
    }

    /// <summary>从归档内提取文件到本地。</summary>
    public void ExtractFile(string path, string destPath)
    {
        string? sevenZip = ContainerDetector.SevenZipPath
            ?? throw new InvalidOperationException("未找到 7-Zip（7z.exe）。");

        var psi = new ProcessStartInfo(sevenZip)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("e");            // 提取到当前目录（不含目录层级）
        psi.ArgumentList.Add(_archivePath);
        psi.ArgumentList.Add(path);
        psi.ArgumentList.Add("-o" + Path.GetDirectoryName(destPath));
        psi.ArgumentList.Add("-y");           // 自动覆盖
        psi.ArgumentList.Add("-bso0");
        psi.ArgumentList.Add("-bsp0");

        using var proc = Process.Start(psi);
        if (proc == null) throw new InvalidOperationException("无法启动 7-Zip。");
        string stdout = proc.StandardOutput.ReadToEnd();
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(120000);

        if (proc.ExitCode != 0)
            throw new InvalidDataException($"提取失败：{(stderr.Trim().Length > 0 ? stderr.Trim() : stdout.Trim())}");

        // 7z e 用归档内原名输出，若与目标名不同则改名
        string produced = Path.Combine(Path.GetDirectoryName(destPath)!, NameOf(path));
        if (!File.Exists(destPath) && File.Exists(produced) &&
            !string.Equals(produced, destPath, StringComparison.OrdinalIgnoreCase))
        {
            File.Move(produced, destPath, overwrite: true);
        }
    }

    public string? GetParentPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        int sep = path.LastIndexOf('\\');
        return sep < 0 ? "" : path[..sep];
    }

    public FileMetadata GetMetadata(string path)
    {
        var e = _entries.FirstOrDefault(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (e == null)
            return new FileMetadata { Name = NameOf(path), FullPath = path };

        return new FileMetadata
        {
            Name = NameOf(e.Path),
            FullPath = e.Path,
            SizeText = e.IsDirectory ? "" : FileItemFormatter.FormatSize(e.Size),
            Size = e.Size,
            CreatedText = "—",
            ModifiedText = FileItemFormatter.FormatTime(e.Modified),
            AccessedText = "—",
            AttributesText = e.Attributes,
            IsDirectory = e.IsDirectory
        };
    }

    public System.Windows.Media.ImageSource? GetIcon(string path, bool isDirectory)
        => isDirectory ? IconProvider.FolderIcon : IconProvider.FromExtension(Path.GetExtension(path));

    /// <summary>
    /// 打开归档内文件：先提取到临时目录，再用系统默认程序打开。
    /// （归档内的文件不在真实文件系统上，必须先落地。）
    /// </summary>
    public void OpenWithDefaultApp(string path)
    {
        var e = _entries.FirstOrDefault(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (e == null || e.IsDirectory)
            throw new FileNotFoundException("归档内找不到该文件。", path);

        string dir = Path.Combine(Path.GetTempPath(), "BasaltOpen");
        Directory.CreateDirectory(dir);
        string temp = Path.Combine(dir, NameOf(path));
        ExtractFile(path, temp);
        Process.Start(new ProcessStartInfo(temp) { UseShellExecute = true });
    }

    public List<BreadcrumbItem> BuildBreadcrumb(string path)
    {
        var list = new List<BreadcrumbItem>
        {
            new() { Display = $"{DisplayName}（{ContainerDetector.DisplayName(Format)}）", Path = "" }
        };

        if (!string.IsNullOrEmpty(path))
        {
            string acc = "";
            foreach (var seg in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                acc = acc.Length == 0 ? seg : acc + "\\" + seg;
                list.Add(new BreadcrumbItem { Display = seg, Path = acc });
            }
        }

        // 始终标记最后一项：它既是"当前级"，也是最后一级（不显示尾部分隔符）
        if (list.Count > 0)
        {
            var last = list[^1];
            list[^1] = new BreadcrumbItem { Display = last.Display, Path = last.Path, IsCurrent = true, IsLast = true };
        }
        return list;
    }

    public void Dispose()
    {
        // 7z 是外部进程，无需释放资源
    }

    /// <summary>容器内的文件不在真实磁盘上 —— 先用 7z 提取到临时目录。</summary>
    public string MaterializeToTemp(string path)
    {
        string dir = Path.Combine(Path.GetTempPath(), "BasaltOpen");
        Directory.CreateDirectory(dir);
        string temp = Path.Combine(dir, NameOf(path));
        ExtractFile(path, temp);
        return temp;
    }
}
