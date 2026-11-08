using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Basalt.Services;

public enum PreviewKind { Text, Image, Hex, External }

/// <summary>文本读取结果（含截断标记与编码名，供预览窗口状态栏显示）。</summary>
public sealed record TextPreview(string Text, bool Truncated, string EncodingName, int BytesRead, int LineCount);

/// <summary>
/// 双引擎智能预览分发器。
/// 所有内部预览都只通过 IVirtualDisk.OpenRead 拿 Stream（VHD 物理路径 / VMDK 文件系统流 / 容器 7z -so 内存流），
/// 绝不把文本/图片/二进制偷懒丢给 Process.Start。
/// 只有 .docx / .pdf 等确实无法内部渲染的复杂格式才走外部程序。
/// </summary>
public static class PreviewDispatcher
{
    private const int MaxTextBytes = 2 * 1024 * 1024;
    private const int MaxTextLines = 5000;
    private const int HexBytes = 4096;
    private const int SniffBytes = 100;
    private const int BinaryControlPercent = 10;

    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".md", ".markdown", ".json", ".jsonc", ".xml", ".xsd", ".xsl", ".xslt",
        ".cs", ".csproj", ".xaml", ".sln", ".props", ".targets", ".config", ".resx", ".manifest",
        ".ini", ".cfg", ".conf", ".log", ".csv", ".tsv", ".yml", ".yaml", ".toml", ".properties",
        ".bat", ".cmd", ".ps1", ".sh", ".bash", ".py", ".js", ".ts", ".jsx", ".tsx", ".java",
        ".c", ".h", ".cpp", ".hpp", ".cc", ".rs", ".go", ".rb", ".php", ".sql", ".html", ".htm",
        ".css", ".scss", ".less", ".svg", ".gitignore", ".gitattributes", ".editorconfig",
        ".vcxproj", ".gradle", ".lock", ".nfo", ".diz", ".srt", ".vtt", ".lua", ".pl", ".swift",
        ".kt", ".vue", ".razor", ".asm", ".s", ".make", ".mk", ".dockerfile"
    };

    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jpe", ".bmp", ".ico", ".gif", ".tif", ".tiff"
    };

    private static readonly HashSet<string> ExternalExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".odt", ".ods", ".odp", ".rtf", ".epub", ".mobi", ".chm",
        ".mp3", ".wav", ".flac", ".aac", ".ogg", ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".webm"
    };

    public static PreviewKind Classify(string fileName)
    {
        string ext = Path.GetExtension(fileName);
        if (TextExt.Contains(ext)) return PreviewKind.Text;
        if (ImageExt.Contains(ext)) return PreviewKind.Image;
        if (ExternalExt.Contains(ext)) return PreviewKind.External;
        if (string.IsNullOrEmpty(fileName)) return PreviewKind.Hex;
        if (fileName.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith(".", StringComparison.Ordinal) ||
            fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            return PreviewKind.Text;
        return PreviewKind.Hex;
    }

    /// <summary>
    /// 内容嗅探：出现空字节，或控制字符（TAB/LF/CR 除外）占比达到阈值，即判为二进制。
    /// UTF-16 BOM 文本天然含 \0，作为白名单保留既有 UTF-16 解码能力。
    /// </summary>
    public static bool IsBinary(ReadOnlySpan<byte> head)
    {
        if (head.Length == 0) return false;
        if (head.Length >= 2 &&
            ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF)))
            return false;

        int control = 0;
        foreach (byte b in head)
        {
            if (b == 0) return true;
            if ((b < 0x20 && b is not (0x09 or 0x0A or 0x0D)) || b == 0x7F) control++;
        }
        return control * 100 >= head.Length * BinaryControlPercent;
    }

    /// <summary>按分类打开对应预览窗口；返回一行状态栏文案。无界面自测时只返回描述，不建窗口。</summary>
    public static string Preview(IVirtualDisk disk, string path, string name)
    {
        var kind = Classify(name);
        Window? owner = Application.Current?.MainWindow;

        switch (kind)
        {
            case PreviewKind.Text:
            {
                using var s = disk.OpenRead(path);
                byte[] sniff = ReadHead(s, SniffBytes);
                if (IsBinary(sniff))
                {
                    // 二进制内容绝不交给文本窗口解码；把已嗅探的头部接上前 4KB 直接转 Hex。
                    byte[] head = ReadHead(s, HexBytes, sniff);
                    if (!App.Headless)
                        new HexViewerWindow(name, head) { Owner = owner }.Show();
                    return $"内部十六进制预览 {name} — 检测到二进制内容，自动降级，前 {head.Length} 字节";
                }

                var result = ReadText(s, sniff);
                if (!App.Headless)
                    new TextViewerWindow(name, result) { Owner = owner }.Show();
                return $"内部文本预览 {name} — {result.EncodingName}，{result.LineCount} 行" +
                       (result.Truncated ? "（已截断）" : "");
            }

            case PreviewKind.Image:
            {
                using var s = disk.OpenRead(path);
                var image = LoadImage(s);
                if (!App.Headless)
                    new ImageViewerWindow(name, image) { Owner = owner }.Show();
                return $"内部图片预览 {name} — {image.PixelWidth}×{image.PixelHeight}（纯内存流）";
            }

            case PreviewKind.Hex:
            {
                using var s = disk.OpenRead(path);
                byte[] head = ReadHead(s, HexBytes);
                if (!App.Headless)
                    new HexViewerWindow(name, head) { Owner = owner }.Show();
                return $"内部十六进制预览 {name} — 前 {head.Length} 字节";
            }

            default:
            {
                // 兜底：VHD 直接把真实路径交给 shell；VMDK / 容器由各自引擎先落地到临时文件（退出时清理）。
                disk.OpenWithDefaultApp(path);
                return $"已用系统默认程序打开 {name}（复杂格式）";
            }
        }
    }

    /// <summary>读取文本：上限 2MB / 5000 行，自动识别 BOM 与 GBK。prefix 为已嗅探消耗的文件头。</summary>
    public static TextPreview ReadText(Stream src, byte[]? prefix = null)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int total = 0;
        int n;
        if (prefix is { Length: > 0 })
        {
            buffer.Write(prefix, 0, prefix.Length);
            total = prefix.Length;
        }
        while (total < MaxTextBytes &&
               (n = src.Read(chunk, 0, Math.Min(chunk.Length, MaxTextBytes - total))) > 0)
        {
            buffer.Write(chunk, 0, n);
            total += n;
        }

        bool byteTruncated = total >= MaxTextBytes;
        byte[] raw = buffer.ToArray();
        string text = DecodeText(raw, byteTruncated, out string encName);

        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = text.Split('\n');
        bool lineTruncated = lines.Length > MaxTextLines;
        if (lineTruncated) text = string.Join("\n", lines.Take(MaxTextLines));

        return new TextPreview(text, byteTruncated || lineTruncated, encName, total,
            lineTruncated ? MaxTextLines : lines.Length);
    }

    private static string DecodeText(byte[] raw, bool truncated, out string encName)
    {
        encName = "UTF-8";
        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
        {
            encName = "UTF-8 (BOM)";
            return Encoding.UTF8.GetString(raw, 3, raw.Length - 3);
        }
        if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
        {
            encName = "UTF-16 LE";
            return Encoding.Unicode.GetString(raw, 2, raw.Length - 2);
        }
        if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
        {
            encName = "UTF-16 BE";
            return Encoding.BigEndianUnicode.GetString(raw, 2, raw.Length - 2);
        }

        try
        {
            encName = "UTF-8";
            return new UTF8Encoding(false, true).GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            // 截断处正好切断多字节字符时不该误判成 GBK
            if (truncated) return new UTF8Encoding(false, false).GetString(raw);
            encName = "GB18030";
            return Encoding.GetEncoding(54936).GetString(raw);
        }
    }

    /// <summary>纯内存流转 BitmapImage：Stream 不落地、不依赖文件路径。</summary>
    public static BitmapImage LoadImage(Stream src)
    {
        var ms = new MemoryStream();
        src.CopyTo(ms);
        ms.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad; // EndInit 后即可释放 Stream
        image.StreamSource = ms;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>读取前 count 字节；prefix 为已消耗的文件头，拼在结果开头，避免重复读流。</summary>
    public static byte[] ReadHead(Stream src, int count, byte[]? prefix = null)
    {
        var buffer = new byte[count];
        int read = 0, n;
        if (prefix is { Length: > 0 })
        {
            read = Math.Min(prefix.Length, count);
            Array.Copy(prefix, 0, buffer, 0, read);
        }
        while (read < count && (n = src.Read(buffer, read, count - read)) > 0) read += n;
        if (read != count) Array.Resize(ref buffer, read);
        return buffer;
    }

    /// <summary>16 字节一行的简易十六进制视图：偏移 + HEX + ASCII。</summary>
    public static string FormatHex(byte[] data)
    {
        var sb = new StringBuilder();
        for (int offset = 0; offset < data.Length; offset += 16)
        {
            sb.Append(offset.ToString("X8")).Append("  ");
            var ascii = new StringBuilder();
            for (int i = 0; i < 16; i++)
            {
                if (i == 8) sb.Append(' ');
                if (offset + i < data.Length)
                {
                    byte b = data[offset + i];
                    sb.Append(b.ToString("X2")).Append(' ');
                    ascii.Append(b is >= 0x20 and <= 0x7E ? (char)b : '.');
                }
                else
                {
                    sb.Append("   ");
                    ascii.Append(' ');
                }
            }
            sb.Append(" |").Append(ascii).Append('|').Append('\n');
        }
        return sb.ToString();
    }
}
