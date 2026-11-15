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

    /// <summary>归档在真实文件系统上的路径（ISO 检查器需要据此自建独立引擎）。</summary>
    public string ArchivePath => _archivePath;

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
        psi.ArgumentList.Add("-sccUTF-8");    // 强制 7z 以 UTF-8 输出文件名，避免中文变 OEM 乱码
        psi.ArgumentList.Add(_archivePath);

        using var proc = Process.Start(psi);
        if (proc == null) throw new InvalidOperationException("无法启动 7-Zip。");

        // 按原始字节读取，再按 UTF-8 → GB18030 依次解码：
        // 老归档没有 UTF-8 标志时 7z 仍会吐 OEM/GBK 字节，直接用 StreamReader 会乱码。
        var stderrTask = proc.StandardError.ReadToEndAsync();
        byte[] raw;
        using (var buffer = new MemoryStream())
        {
            proc.StandardOutput.BaseStream.CopyTo(buffer);
            raw = buffer.ToArray();
        }
        string stderr = stderrTask.GetAwaiter().GetResult();
        proc.WaitForExit(60000);

        if (proc.ExitCode != 0)
            throw new InvalidDataException($"7-Zip 无法读取该归档：{stderr.Trim()}");

        ParseEntries(DecodeListing(raw));
    }

    /// <summary>列目录输出解码：优先严格 UTF-8，失败回退 GB18030（GBK 超集）。</summary>
    private static string DecodeListing(byte[] raw)
    {
        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
            raw = raw[3..];
        try
        {
            return new UTF8Encoding(false, true).GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(54936).GetString(raw);
        }
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
                    Size = e.Size,
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
                Size = e.Size,
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

        string temp = TempFileRegistry.NewPath(NameOf(path));
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
        string temp = TempFileRegistry.NewPath(NameOf(path));
        ExtractFile(path, temp);
        return temp;
    }

    /// <summary>
    /// 容器模式：7z e -so 把单个文件直接写到 stdout，读进 MemoryStream，全程不落地。
    /// 供内部预览（文本/图片/十六进制）使用。
    /// </summary>
    public Stream OpenRead(string path)
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
        psi.ArgumentList.Add("e");
        psi.ArgumentList.Add("-so");          // 输出到 stdout，不写磁盘
        psi.ArgumentList.Add(_archivePath);
        psi.ArgumentList.Add(path);
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-bso0");        // 关闭进度/日志输出，避免混进二进制数据
        psi.ArgumentList.Add("-bsp0");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 7-Zip。");

        var stderrTask = proc.StandardError.ReadToEndAsync();
        using var buffer = new MemoryStream();
        proc.StandardOutput.BaseStream.CopyTo(buffer);
        string stderr = stderrTask.GetAwaiter().GetResult();
        proc.WaitForExit(120000);

        if (proc.ExitCode != 0)
            throw new InvalidDataException($"读取归档内文件失败：{(stderr.Trim().Length > 0 ? stderr.Trim() : "7-Zip 返回非零退出码")}");

        var result = new MemoryStream(buffer.ToArray(), writable: false);
        return result;
    }

    /// <summary>
    /// 容器引擎的头部预读：7z e -so 只取前 maxBytes 字节就终止进程。
    /// OpenRead 会把整个文件收进 MemoryStream —— 对 4~6 GB 的 install.wim 不可接受。
    /// </summary>
    public byte[] ReadHead(string path, int maxBytes)
    {
        // ISO / UDF 可直接 seek，不必启动 7-Zip 再提前 Kill（也避免管道未排空时的报错）
        if (Format == ContainerFormat.Iso)
        {
            var fast = IsoRandomAccess.TryReadRange(_archivePath, path, 0, maxBytes);
            if (fast != null) return fast;
        }

        string? sevenZip = ContainerDetector.SevenZipPath
            ?? throw new InvalidOperationException("未找到 7-Zip（7z.exe）。");

        var psi = new ProcessStartInfo(sevenZip)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("e");
        psi.ArgumentList.Add("-so");
        psi.ArgumentList.Add(_archivePath);
        psi.ArgumentList.Add(path);
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-bso0");
        psi.ArgumentList.Add("-bsp0");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 7-Zip。");

        // 异步排空 stderr，避免 7z 写错误输出时管道满而阻塞
        _ = proc.StandardError.ReadToEndAsync();

        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int left = maxBytes;
        while (left > 0)
        {
            int n = proc.StandardOutput.BaseStream.Read(chunk, 0, Math.Min(chunk.Length, left));
            if (n <= 0) break;
            buffer.Write(chunk, 0, n);
            left -= n;
        }

        if (left == 0)
        {
            // 已读够，提前结束 7z，不让它继续吐剩余 GB 数据
            try { proc.Kill(entireProcessTree: true); } catch { /* 已自行退出 */ }
        }
        else
        {
            try { proc.WaitForExit(5000); } catch { /* 超时由 Dispose 收尾 */ }
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// 容器内的随机读取：ISO 走 UDF 可寻址快路径，其它格式用 7z -so 顺序跳过。
    /// <para>
    /// 真实 Windows ISO 的 install.wim XML 在 4.9 GB 处，顺序跳过需要 45 秒左右，
    /// 因此 ISO 必须由 IsoRandomAccess 接管；非光盘容器若偏移过大则明确拒绝并降级。
    /// </para>
    /// </summary>
    public byte[] ReadRange(string path, long offset, int count)
    {
        if (count <= 0 || offset < 0) return Array.Empty<byte>();

        if (Format == ContainerFormat.Iso)
        {
            var fast = IsoRandomAccess.TryReadRange(_archivePath, path, offset, count);
            if (fast != null) return fast;
        }

        const long MaxDrainBytes = 256L << 20;
        if (offset > MaxDrainBytes)
            throw new InvalidDataException(
                $"元数据位于文件 {FileItemFormatter.FormatSize(offset)} 处，当前容器不支持随机访问；" +
                "可先提取安装映像到本地再检查。");

        string? sevenZip = ContainerDetector.SevenZipPath
            ?? throw new InvalidOperationException("未找到 7-Zip（7z.exe）。");

        var psi = new ProcessStartInfo(sevenZip)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("e");
        psi.ArgumentList.Add("-so");
        psi.ArgumentList.Add(_archivePath);
        psi.ArgumentList.Add(path);
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-bso0");
        psi.ArgumentList.Add("-bsp0");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 7-Zip。");
        _ = proc.StandardError.ReadToEndAsync();

        var skip = new byte[64 * 1024];
        long left = offset;
        while (left > 0)
        {
            int n = proc.StandardOutput.BaseStream.Read(skip, 0, (int)Math.Min(skip.Length, left));
            if (n <= 0) break;
            left -= n;
        }
        if (left > 0)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return Array.Empty<byte>();       // 文件比偏移还短
        }

        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = proc.StandardOutput.BaseStream.Read(buffer, read, count - read);
            if (n <= 0) break;
            read += n;
        }
        try { proc.Kill(entireProcessTree: true); } catch { }
        if (read != count) Array.Resize(ref buffer, read);
        return buffer;
    }
}
