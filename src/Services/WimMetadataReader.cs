using System.Buffers.Binary;
using System.Text;
using System.Xml.Linq;
using Basalt.Models;

namespace Basalt.Services;

/// <summary>WIM / ESD XML 元数据中的一个 Windows 映像。</summary>
public sealed class WindowsImageInfo
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Edition { get; init; } = "";
    public string Version { get; init; } = "";
    public string Architecture { get; init; } = "";
    public string SizeText { get; init; } = "";

    public string IndexLabel => $"#{Index}";

    public string Subtitle => string.Join(" · ", new[] { Version, Architecture, SizeText, Edition }
        .Where(s => s.Length > 0));
}

/// <summary>由 208 字节 WIM 头解出的 XML 资源位置。</summary>
public sealed class WimXmlLocation
{
    public long Offset { get; set; }
    public long Size { get; set; }
    public int HeaderImageCount { get; set; }
    public string Compression { get; set; } = "";
    public string? Note { get; set; }

    public bool Success => Note == null;

    /// <summary>人可读的位置描述，用于诊断报告。</summary>
    public string OffsetText => FileItemFormatter.FormatSize(Offset);
}

/// <summary>WIM 元数据读取结果。Note 为 null 表示 XML 解析成功。</summary>
public sealed class WimReadResult
{
    public int HeaderImageCount { get; set; }
    public string Compression { get; set; } = "";
    public List<WindowsImageInfo> Images { get; } = new();
    public string? Note { get; set; }

    /// <summary>XML 的来源（头部预读 / 尾部随机读取 / 顺序流式），用于诊断与 UI 摘要。</summary>
    public string XmlSource { get; set; } = "";

    public bool Success => Note == null;

    public string Summary => Success
        ? $"已解析 XML 元数据 · {Images.Count} 个映像 · 压缩 {Compression}" +
          (XmlSource.Length > 0 ? $" · {XmlSource}" : "")
        : $"未解析 XML 元数据 · {Note}";
}

/// <summary>
/// 纯托管 WIM / ESD 元数据读取器：只读文件头 + 未压缩的 XML 资源，不解压任何映像数据。
/// <para>
/// 依据 wimlib 的 on-disk 结构（WIM_HEADER_DISK_SIZE = 208）：
/// rhXmlData 位于 +0x48，是 24 字节 wim_reshdr_disk ——
/// [0..6] size_in_wim（56 位）、[7] flags、[8..15] offset_in_wim、[16..23] uncompressed_size。
/// XML 资源恒为未压缩的 UTF-16LE 文本（带 FF FE BOM）；若 flags 带 COMPRESSED(0x04) / SOLID(0x10)，
/// 说明是 ESD 的 LZMS 压缩资源，纯托管不处理，交由调用方降级为文件列表。
/// </para>
/// <para>
/// <strong>两阶段读取</strong>：真实 Windows ISO 的 install.wim 有 4~6 GB，XML 写在文件尾部
/// （实测 Win11 22H2 在 4.93 GB 处）。因此先只用 208 字节头 <see cref="LocateXml"/> 拿到绝对偏移，
/// 再由调用方用 <c>IVirtualDisk.ReadRange</c> 精确取回那 16 KB XML，最后交给 <see cref="Read"/> 解析。
/// </para>
/// </summary>
public static class WimMetadataReader
{
    public const int HeaderSize = 208;
    private const int XmlReshdrOffset = 0x48;
    private const long MaxXmlBytes = 4L << 20;

    private const byte FlagCompressed = 0x04;
    private const byte FlagSolid = 0x10;

    /// <summary>
    /// 第一阶段：从文件头定位 XML 资源。只校验魔数 / 标志 / 尺寸，不要求 XML 已在缓冲区内。
    /// </summary>
    public static WimXmlLocation LocateXml(byte[] head)
    {
        var loc = new WimXmlLocation();

        if (head == null || head.Length < HeaderSize)
        {
            loc.Note = "预读内容不足 208 字节，无法识别为 WIM / ESD";
            return loc;
        }

        if (!(head[0] == 'M' && head[1] == 'S' && head[2] == 'W' && head[3] == 'I' && head[4] == 'M' &&
              head[5] == 0 && head[6] == 0 && head[7] == 0))
        {
            loc.Note = "不是标准 WIM / ESD 文件（MSWIM 魔数不匹配）";
            return loc;
        }

        uint flags = U32(head, 0x10);
        loc.HeaderImageCount = (int)U32(head, 0x2C);
        loc.Compression = CompressionName(flags);

        byte xmlFlags = head[XmlReshdrOffset + 7];
        ulong rawOffset = U64(head, XmlReshdrOffset + 8);
        ulong rawSize = U64(head, XmlReshdrOffset + 16);

        if ((xmlFlags & (FlagCompressed | FlagSolid)) != 0)
        {
            loc.Note = "XML 为压缩 / 固态资源（常见于 install.esd 的 LZMS），纯托管解析暂不支持";
            return loc;
        }
        if (rawOffset == 0 || rawSize == 0)
        {
            loc.Note = "WIM 头未给出 XML 资源位置";
            return loc;
        }
        if (rawSize > (ulong)MaxXmlBytes)
        {
            loc.Note = $"XML 资源过大（{rawSize} 字节），已跳过";
            return loc;
        }
        if (rawOffset > long.MaxValue)
        {
            loc.Note = "XML 资源偏移超出可寻址范围";
            return loc;
        }

        loc.Offset = (long)rawOffset;
        loc.Size = (long)rawSize;
        return loc;
    }

    /// <summary>
    /// 第二阶段：解析 XML 得到版本列表。
    /// <paramref name="externalXml"/> 为按 <see cref="WimXmlLocation.Offset"/> 随机读回的 XML 字节；
    /// 为 null 时从 <paramref name="head"/> 里裁切（兼容"XML 就在文件开头"的合成夹具 / 小 WIM）。
    /// </summary>
    public static WimReadResult Read(byte[] head, byte[]? externalXml = null, string xmlSource = "")
    {
        var loc = LocateXml(head);
        var result = new WimReadResult
        {
            HeaderImageCount = loc.HeaderImageCount,
            Compression = loc.Compression,
            XmlSource = xmlSource
        };
        if (!loc.Success) return Fail(result, loc.Note!);

        byte[] xmlBytes;
        if (externalXml != null)
        {
            xmlBytes = externalXml;
        }
        else
        {
            if (loc.Offset > head.Length || loc.Offset + loc.Size > head.Length)
                return Fail(result,
                    $"XML 资源超出预读范围（需 {loc.Offset + loc.Size} 字节，已读 {head.Length} 字节）");
            xmlBytes = new byte[(int)loc.Size];
            Buffer.BlockCopy(head, (int)loc.Offset, xmlBytes, 0, (int)loc.Size);
        }

        string xml = DecodeXml(xmlBytes, 0, xmlBytes.Length);
        if (!xml.Contains("<WIM", StringComparison.OrdinalIgnoreCase))
            return Fail(result, "XML 解码结果不含 <WIM> 根元素");

        try
        {
            ParseImages(xml, result);
            if (result.Images.Count == 0)
                return Fail(result, "XML 中没有 <IMAGE> 条目");
        }
        catch (Exception ex)
        {
            return Fail(result, $"XML 解析失败：{ex.Message}");
        }
        return result;
    }

    private static WimReadResult Fail(WimReadResult result, string note)
    {
        result.Note = note;
        return result;
    }

    private static void ParseImages(string xml, WimReadResult result)
    {
        var doc = XDocument.Parse(xml);
        int ordinal = 0;

        foreach (var img in doc.Descendants().Where(e => e.Name.LocalName == "IMAGE"))
        {
            ordinal++;
            int index = int.TryParse(Attr(img, "INDEX"), out var parsed) ? parsed : ordinal;

            string Direct(string n) => img.Elements()
                .FirstOrDefault(e => e.Name.LocalName == n)?.Value.Trim() ?? "";
            var win = img.Elements().FirstOrDefault(e => e.Name.LocalName == "WINDOWS");
            string Win(string n) => win?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == n)?.Value.Trim() ?? "";
            var ver = win?.Elements().FirstOrDefault(e => e.Name.LocalName == "VERSION");
            string Ver(string n) => ver?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == n)?.Value.Trim() ?? "";

            string version = Ver("MAJOR").Length > 0
                ? string.Join(".", new[] { Ver("MAJOR"), Ver("MINOR"), Ver("BUILD") }.Where(s => s.Length > 0))
                : "";
            long total = long.TryParse(Direct("TOTALBYTES"), out var t) ? t : 0;

            result.Images.Add(new WindowsImageInfo
            {
                Index = index,
                Name = FirstNonEmpty(Direct("DISPLAYNAME"), Direct("NAME"), $"映像 {index}"),
                Description = FirstNonEmpty(Direct("DESCRIPTION"), Win("PRODUCTNAME")),
                Edition = Win("EDITIONID"),
                Version = version,
                Architecture = ArchName(Win("ARCH")),
                SizeText = total > 0 ? FileItemFormatter.FormatSize(total) : ""
            });
        }
    }

    /// <summary>WIM XML 恒为 UTF-16LE，通常带 FF FE BOM；尾部可能补 \0。</summary>
    private static string DecodeXml(byte[] data, int offset, int count)
    {
        if (count <= 0) return "";
        if ((count & 1) != 0) count--;   // UTF-16 必须偶数长度

        if (count >= 2 && data[offset] == 0xFE && data[offset + 1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(data, offset + 2, count - 2).Trim('\0', '\uFEFF').Trim();

        if (count >= 2 && data[offset] == 0xFF && data[offset + 1] == 0xFE)
        {
            offset += 2;
            count -= 2;
        }
        return Encoding.Unicode.GetString(data, offset, count).Trim('\0', '\uFEFF').Trim();
    }

    private static string CompressionName(uint flags) => flags switch
    {
        _ when (flags & 0x00080000) != 0 => "LZMS",
        _ when (flags & 0x00040000) != 0 => "LZX",
        _ when (flags & 0x00020000) != 0 => "XPRESS",
        _ when (flags & 0x00000002) != 0 => "已压缩（类型未标注）",
        _ => "无压缩"
    };

    private static string ArchName(string arch) => arch switch
    {
        "0" => "x86",
        "5" => "ARM",
        "6" => "IA64",
        "9" => "x64",
        "12" => "ARM64",
        "" => "",
        _ => $"ARCH {arch}"
    };

    private static string Attr(XElement e, string name) => e.Attributes()
        .FirstOrDefault(a => a.Name.LocalName == name)?.Value ?? "";

    private static string FirstNonEmpty(params string[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    private static uint U32(byte[] b, int off) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(off, 4));
    private static ulong U64(byte[] b, int off) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(off, 8));
}
