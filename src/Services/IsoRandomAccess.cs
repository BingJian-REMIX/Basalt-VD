using System.IO;
using DiscUtils;
using DiscUtils.Iso9660;
using DiscUtils.Udf;

namespace Basalt.Services;

/// <summary>
/// 光盘镜像的可寻址读取：把 ISO / UDF 当作文件系统打开，直接 Seek 到指定偏移读几个字节。
/// <para>
/// 为什么需要它：Windows 安装 ISO 里的 <c>install.wim</c> 通常 4~6 GB，而 WIM 的 XML
/// 元数据（版本列表）写在<strong>文件尾部</strong>。容器引擎原本的 <c>7z e -so</c> 只能
/// 从头顺序输出，跳到 4.9 GB 处需要让 7-Zip 白吐约 5 GB 数据（实测 1 GB ≈ 9 秒），
/// 自动弹出检查器会卡住几十秒。UDF / ISO9660 的文件流支持 Seek，读 16 KB 元数据只需毫秒。
/// </para>
/// <para>
/// 纯托管、与项目现有 DiscUtils 同一版本；UDF 优先（ISO9660 桥对 &gt;4 GB 文件有截断）。
/// 打开失败一律返回 null，调用方回退到顺序流式读取并降级，绝不影响挂载主流程。
/// </para>
/// </summary>
public static class IsoRandomAccess
{
    /// <summary>
    /// 读取光盘镜像内某个条目的 [offset, offset+count) 字节。
    /// 返回 null 表示该镜像不是可识别的光盘格式、条目不存在或读取失败。
    /// </summary>
    public static byte[]? TryReadRange(string isoPath, string entryPath, long offset, int count)
    {
        if (count <= 0 || offset < 0 || !File.Exists(isoPath))
            return null;

        try
        {
            using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 1 << 16, FileOptions.RandomAccess);

            var readers = new List<IFileSystem>();

            // UDF 优先：Windows 官方 ISO 用 UDF 承载 >4 GB 的 install.wim
            try
            {
                fs.Position = 0;
                if (UdfReader.Detect(fs))
                {
                    fs.Position = 0;
                    readers.Add(new UdfReader(fs));
                }
            }
            catch { /* 非 UDF，继续尝试 ISO9660 */ }

            // ISO9660 / Joliet 桥：对小型 ISO（如 PE 工具盘）同样有效
            try
            {
                fs.Position = 0;
                if (CDReader.Detect(fs))
                {
                    fs.Position = 0;
                    readers.Add(new CDReader(fs, true));
                }
            }
            catch { /* 格式不支持 */ }

            foreach (var reader in readers)
            {
                foreach (string candidate in PathVariants(entryPath))
                {
                    byte[]? data = TryReadOne(reader, candidate, offset, count);
                    if (data != null) return data;
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? TryReadOne(IFileSystem fs, string path, long offset, int count)
    {
        try
        {
            if (!fs.FileExists(path)) return null;
            using var s = fs.OpenFile(path, FileMode.Open, FileAccess.Read);
            if (!s.CanSeek || offset >= s.Length) return null;

            s.Position = offset;
            var buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buffer, read, count - read);
                if (n <= 0) break;
                read += n;
            }
            if (read == 0) return null;
            if (read != count) Array.Resize(ref buffer, read);
            return buffer;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>DiscUtils 的路径分隔符 / 大小写 / 前导反斜杠在不同实现里要求不一，逐个尝试。</summary>
    private static IEnumerable<string> PathVariants(string entryPath)
    {
        string p = entryPath.Replace('/', '\\').Trim('\\');
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string v in new[]
                 {
                     "\\" + p, p,
                     "\\" + p.ToUpperInvariant(), p.ToUpperInvariant(),
                     "\\" + p.ToLowerInvariant(), p.ToLowerInvariant()
                 })
        {
            if (seen.Add(v)) yield return v;
        }
    }
}
