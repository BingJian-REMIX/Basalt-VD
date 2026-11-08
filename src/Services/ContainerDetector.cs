using System.IO;

namespace Basalt.Services;

/// <summary>容器（归档/镜像）格式。</summary>
public enum ContainerFormat
{
    None,
    Zip,
    Iso,
    Rar,
    SevenZip,
    Cabinet,
    SelfExtracting,
    Tar,
    Gzip,
    Xz,
    Bzip2,
    Zstd,
}

/// <summary>
/// 容器格式探测器：按文件魔数（magic number）识别，扩展名只作辅助。
/// <para>
/// 为什么要按魔数而不是扩展名：自解压 exe（SFX）扩展名是 .exe，内容其实是 zip/7z；
/// 反过来 .zip 文件里装的东西可能叫 .iso。7-Zip 本身也用魔数识别，两边口径一致。
/// </para>
/// </summary>
public static class ContainerDetector
{
    /// <summary>7-Zip 可执行文件路径（若系统已安装）。</summary>
    private static readonly string[] SevenZipCandidates =
    {
        @"D:\Program Files\7-Zip\7z.exe",
        @"C:\Program Files\7-Zip\7z.exe",
        @"C:\Program Files (x86)\7-Zip\7z.exe",
        @"D:\Program Files (x86)\7-Zip\7z.exe",
    };

    private static string? _sevenZipPath;
    private static bool _sevenZipSearched;

    /// <summary>找到 7z.exe 的路径；找不到返回 null。</summary>
    public static string? SevenZipPath
    {
        get
        {
            if (_sevenZipSearched) return _sevenZipPath;
            _sevenZipSearched = true;
            foreach (var p in SevenZipCandidates)
            {
                if (File.Exists(p)) { _sevenZipPath = p; break; }
            }
            if (_sevenZipPath == null)
            {
                // 再从 PATH 里找
                var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    var candidate = Path.Combine(dir.Trim('"'), "7z.exe");
                    if (File.Exists(candidate)) { _sevenZipPath = candidate; break; }
                }
            }
            return _sevenZipPath;
        }
    }

    /// <summary>按魔数识别容器格式；读取 stream 开头若干字节即可。</summary>
    public static ContainerFormat Detect(Stream stream)
    {
        long saved = stream.Position;
        try
        {
            stream.Position = 0;
            var head = new byte[16];
            int read = stream.Read(head, 0, head.Length);
            if (read < 4) return ContainerFormat.None;

            // ZIP: "PK\x03\x04"（普通）/ "PK\x05\x06"（空）/ "PK\x07\x08"（跨盘）
            if (head[0] == 'P' && head[1] == 'K' &&
                ((head[2] == 3 && head[3] == 4) || (head[2] == 5 && head[3] == 6) || (head[2] == 7 && head[3] == 8)))
                return ContainerFormat.Zip;

            // 7z: "7z\xBC\xAF\x27\x1C"
            if (read >= 6 && head[0] == '7' && head[1] == 'z' && head[2] == 0xBC && head[3] == 0xAF)
                return ContainerFormat.SevenZip;

            // RAR: "Rar!\x1A\x07\x00"（v4）/ "Rar!\x1A\x07\x01\x00"（v5）
            if (read >= 7 && head[0] == 'R' && head[1] == 'a' && head[2] == 'r' && head[3] == '!' && head[4] == 0x1A)
                return ContainerFormat.Rar;

            // CAB: "MSCF"
            if (head[0] == 'M' && head[1] == 'S' && head[2] == 'C' && head[3] == 'F')
                return ContainerFormat.Cabinet;

            // ISO 9660 / UDF：签名不在文件头，要到固定偏移去读（见 DetectIso）
            var iso = DetectIso(stream);
            if (iso != ContainerFormat.None) return iso;

            // GZIP: 1F 8B
            if (head[0] == 0x1F && head[1] == 0x8B)
                return ContainerFormat.Gzip;

            // XZ: FD 37 7A 58 5A 00
            if (read >= 6 && head[0] == 0xFD && head[1] == 0x37 && head[2] == 'z' && head[3] == 'X' && head[4] == 'Z')
                return ContainerFormat.Xz;

            // BZIP2: "BZh"
            if (head[0] == 'B' && head[1] == 'Z' && head[2] == 'h')
                return ContainerFormat.Bzip2;

            // Zstandard: 28 B5 2F FD
            if (read >= 4 && head[0] == 0x28 && head[1] == 0xB5 && head[2] == 0x2F && head[3] == 0xFD)
                return ContainerFormat.Zstd;

            // TAR: 偏移 257 处 "ustar"
            if (read >= 12 && head[0] == 'u' && head[1] == 's' && head[2] == 't' && head[3] == 'a' && head[4] == 'r')
                return ContainerFormat.Tar;

            // PE 可执行文件（SFX 候选）：MZ 头
            if (head[0] == 'M' && head[1] == 'Z')
                return ContainerFormat.SelfExtracting;
        }
        catch { /* 读不到就当不是容器 */ }
        finally
        {
            stream.Position = saved;
        }
        return ContainerFormat.None;
    }

    /// <summary>检测 ISO 镜像：ISO9660 主卷描述符在偏移 32769，UDF 锚点在偏移 256。</summary>
    private static ContainerFormat DetectIso(Stream stream)
    {
        try
        {
            // UDF 锚点卷描述符：偏移 256
            stream.Position = 256;
            var udf = new byte[5];
            if (stream.Read(udf, 0, 5) == 5 &&
                udf[0] == 'B' && udf[1] == 'E' && udf[2] == 'A' && udf[3] == '0' && udf[4] == '1')
                return ContainerFormat.Iso;

            // ISO9660 主卷描述符：偏移 32769（0x8001），签名 "CD001"（5 字节，其后是版本号）
            stream.Position = 32769;
            var iso = new byte[5];
            if (stream.Read(iso, 0, 5) == 5 &&
                iso[0] == 'C' && iso[1] == 'D' && iso[2] == '0' && iso[3] == '0' && iso[4] == '1')
                return ContainerFormat.Iso;
        }
        catch { /* 不是 ISO */ }
        return ContainerFormat.None;
    }

    /// <summary>检测真实磁盘上的文件格式。</summary>
    public static ContainerFormat DetectFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return Detect(fs);
        }
        catch
        {
            return ContainerFormat.None;
        }
    }

    /// <summary>
    /// 判断是否为"可以当容器展开浏览"的文件。
    /// SFX（自解压 exe）要看内嵌归档偏移才能确定，交给 7-Zip 判定更准 —— 这里只排除纯 PE。
    /// </summary>
    public static bool IsContainerFile(string path)
    {
        var fmt = DetectFile(path);
        return fmt switch
        {
            ContainerFormat.None => false,
            // 自解压 exe 要进一步确认内嵌的是不是归档，交给 7z 的探测
            ContainerFormat.SelfExtracting => IsSfxArchive(path),
            _ => true
        };
    }

    /// <summary>自解压 exe：让 7-Zip 尝试以归档方式打开，成功即视为 SFX。</summary>
    private static bool IsSfxArchive(string path)
    {
        string? sevenZip = SevenZipPath;
        if (sevenZip == null) return false;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(sevenZip)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("l");
            psi.ArgumentList.Add("-slt");
            psi.ArgumentList.Add(path);
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return false;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(15000);
            // 成功列出内容才会出现条目分隔标记
            return output.Contains("Path = ") && proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>格式的中文显示名。</summary>
    public static string DisplayName(ContainerFormat fmt) => fmt switch
    {
        ContainerFormat.Zip => "ZIP 归档",
        ContainerFormat.Iso => "ISO 镜像",
        ContainerFormat.Rar => "RAR 归档",
        ContainerFormat.SevenZip => "7z 归档",
        ContainerFormat.Cabinet => "CAB 归档",
        ContainerFormat.SelfExtracting => "自解压程序",
        ContainerFormat.Tar => "TAR 归档",
        ContainerFormat.Gzip => "GZIP 压缩",
        ContainerFormat.Xz => "XZ 压缩",
        ContainerFormat.Bzip2 => "BZIP2 压缩",
        ContainerFormat.Zstd => "Zstandard 压缩",
        _ => "归档"
    };
}
