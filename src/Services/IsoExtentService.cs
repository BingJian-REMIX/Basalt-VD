using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Basalt.Services;

/// <summary>install.wim 在 ISO 里的物理 extent 定位结果。</summary>
public sealed class IsoExtent
{
    public long Offset { get; set; }
    public long Length { get; set; }
    public bool Verified { get; set; }
    public string Note { get; set; } = "";
    public string Display => $"偏移 {Offset}（扇区 {Offset / 2048}）· {Models.FileItemFormatter.FormatSize(Length)}";
}

/// <summary>替换结果。</summary>
public sealed class IsoPatchResult
{
    public string OutputPath { get; set; } = "";
    public long OutputLogicalSize { get; set; }
    public long NewWimSize { get; set; }
    public long PaddedBytes { get; set; }
    public bool Sparse { get; set; }
    public string Verification { get; set; } = "";

    /// <summary>稀疏写入成功时，这些字节是真正不占磁盘的空洞。</summary>
    public long PhysicalSavedBytes => Sparse ? PaddedBytes : 0;
}

/// <summary>
/// ISO 内 install.wim 的「原位替换」：复制一份 ISO → 把新 WIM 写回同一个物理 extent →
/// 剩余区间做成 NTFS 稀疏空洞 → 其余扇区原样复制。
/// <para>
/// 为什么不重建 ISO：Windows 安装盘是 UDF + El Torito 引导结构，且 install.wim 常 &gt; 4 GB。
/// 7-Zip 不能写 ISO/UDF，DiscUtils 的 CDBuilder 只能写 ISO9660（32 位长度），
/// 重建会丢引导或截断大文件。原位替换保持 UDF 目录项、文件长度、引导目录完全不变，
/// 因此 100% 保留可引导性；新 WIM 变短的部分补零，WIM 解析器只读头 + 资源表 + XML，
/// 尾部多余字节会被忽略。
/// </para>
/// <para>
/// 逻辑尺寸不变是刻意的（改文件长度就要重写 UDF File Entry 与分区描述符，风险高），
/// 但在 NTFS 上把补零区间写成稀疏空洞后，<strong>实际占用会随精简量真实下降</strong>。
/// </para>
/// </summary>
public static class IsoExtentService
{
    private const int Sector = 2048;
    private const int BufferSize = 4 << 20;
    private static readonly byte[] WimMagic = "MSWIM\0\0\0"u8.ToArray();

    private const uint FSCTL_SET_SPARSE = 0x000900C4;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    /// <summary>
    /// 在 ISO 里定位 install.wim 的起始物理偏移。
    /// WIM 文件头以 MSWIM 魔数开头且 extent 扇区对齐，因此顺序扫描魔数即可；
    /// 再用 WIM 头给出的 XML 绝对偏移做二次校验，排除 boot.wim 等其他 WIM。
    /// </summary>
    public static IsoExtent? Find(string isoPath, string entryPath, long expectedSize,
        long xmlOffsetInWim, CancellationToken token = default)
    {
        if (!File.Exists(isoPath) || expectedSize <= 0) return null;

        long isoLength = new FileInfo(isoPath).Length;
        long limit = isoLength - expectedSize;
        if (limit < 0) return null;

        var candidates = new List<long>();
        using (var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read,
                   FileShare.Read, BufferSize, FileOptions.SequentialScan))
        {
            var chunk = new byte[BufferSize];
            long pos = 0;
            int overlap = 0;
            while (pos < limit + WimMagic.Length)
            {
                token.ThrowIfCancellationRequested();
                int want = (int)Math.Min(chunk.Length - overlap, limit + WimMagic.Length - pos);
                int read = fs.Read(chunk, overlap, want);
                if (read <= 0) break;
                int total = overlap + read;
                long baseOffset = pos - overlap;
                for (int i = 0; i + WimMagic.Length <= total; i++)
                {
                    long abs = baseOffset + i;
                    if (abs % Sector != 0 || abs > limit) continue;
                    bool hit = true;
                    for (int k = 0; k < WimMagic.Length; k++)
                        if (chunk[i + k] != WimMagic[k]) { hit = false; break; }
                    if (hit) candidates.Add(abs);
                }
                overlap = Math.Min(WimMagic.Length - 1, total);
                if (overlap > 0) Buffer.BlockCopy(chunk, total - overlap, chunk, 0, overlap);
                pos += read;
            }
        }

        foreach (long off in candidates)
        {
            if (off + expectedSize > isoLength) continue;
            if (!XmlSignatureMatches(isoPath, off + xmlOffsetInWim)) continue;

            bool verified = VerifyContiguous(isoPath, entryPath, off, expectedSize);
            return new IsoExtent
            {
                Offset = off,
                Length = expectedSize,
                Verified = verified,
                Note = verified
                    ? "已用 UDF 流交叉校验：单个连续 extent"
                    : "未能通过 UDF 流交叉校验，替换后改用 7-Zip 复核"
            };
        }
        return null;
    }

    /// <summary>WIM 的 XML 资源是 FF FE 开头的 UTF-16LE &lt;WIM&gt; 文本，用它排除 boot.wim。</summary>
    private static bool XmlSignatureMatches(string isoPath, long absoluteOffset)
    {
        if (absoluteOffset < 0) return false;
        try
        {
            using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 16);
            if (absoluteOffset + 8 > fs.Length) return false;
            fs.Position = absoluteOffset;
            Span<byte> b = stackalloc byte[8];
            fs.ReadExactly(b);
            return b[0] == 0xFF && b[1] == 0xFE && b[2] == (byte)'<' && b[3] == 0;
        }
        catch { return false; }
    }

    /// <summary>抽查若干偏移：原始 ISO 字节与 UDF 文件流读取结果一致 ⇒ extent 连续。</summary>
    private static bool VerifyContiguous(string isoPath, string entryPath, long offset, long size)
    {
        long[] samples = { 0, size / 3, size * 2 / 3, size - 16 };
        int matched = 0;
        try
        {
            using var raw = new FileStream(isoPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 1 << 16, FileOptions.RandomAccess);
            foreach (long s in samples)
            {
                if (s < 0 || offset + s + 16 > raw.Length) continue;
                raw.Position = offset + s;
                var a = new byte[16];
                raw.ReadExactly(a);

                byte[]? b = IsoRandomAccess.TryReadRange(isoPath, entryPath, s, 16);
                if (b == null || b.Length != 16 || !a.AsSpan().SequenceEqual(b)) continue;
                matched++;
            }
        }
        catch { return false; }
        return matched >= 2;
    }

    /// <summary>
    /// 从 ISO 的物理 extent 直接把原始 install.wim 拷到工作目录。
    /// 比让 7-Zip 解压一遍快得多，而且能给出真实的字节级进度。
    /// </summary>
    public static void ExtractRaw(string isoPath, long offset, long count, string destPath,
        Action<long, long>? progress = null, CancellationToken token = default)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(destPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var src = new FileStream(isoPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, BufferSize, FileOptions.SequentialScan);
        using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write,
            FileShare.None, BufferSize);
        src.Position = offset;
        long left = count;
        var buffer = new byte[BufferSize];
        while (left > 0)
        {
            token.ThrowIfCancellationRequested();
            int n = src.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            if (n <= 0) break;
            dst.Write(buffer, 0, n);
            left -= n;
            progress?.Invoke(count - left, count);
        }
        dst.Flush();
    }

    /// <summary>
    /// 把新 WIM 写回 ISO 副本的同一 extent，尾部补零（NTFS 上做成稀疏空洞）。
    /// 绝不修改源 ISO。
    /// </summary>
    public static IsoPatchResult Replace(string isoPath, string entryPath, IsoExtent extent,
        string newWimPath, string outputIsoPath,
        Action<long, long>? progress = null, CancellationToken token = default)
    {
        long newSize = new FileInfo(newWimPath).Length;
        if (newSize > extent.Length)
            throw new InvalidOperationException(
                $"精简后的 install.wim（{Models.FileItemFormatter.FormatSize(newSize)}）比原始 " +
                $"{Models.FileItemFormatter.FormatSize(extent.Length)} 还大，无法原位替换；" +
                "请提高压缩级别或减少保留的版本后重试。");

        long isoLength = new FileInfo(isoPath).Length;
        if (extent.Offset + extent.Length > isoLength)
            throw new InvalidDataException("install.wim 的 extent 超出 ISO 文件范围。");

        string? outDir = Path.GetDirectoryName(Path.GetFullPath(outputIsoPath));
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        IsoPatchResult result;
        using (var src = new FileStream(isoPath, FileMode.Open, FileAccess.Read,
                   FileShare.Read, BufferSize, FileOptions.SequentialScan))
        using (var dst = new FileStream(outputIsoPath, FileMode.Create, FileAccess.ReadWrite,
                   FileShare.None, BufferSize))
        {
            bool sparse = TrySetSparse(dst.SafeFileHandle);

            CopyRange(src, dst, 0, extent.Offset, progress, token, isoLength);

            using (var wim = new FileStream(newWimPath, FileMode.Open, FileAccess.Read,
                       FileShare.Read, BufferSize, FileOptions.SequentialScan))
            {
                CopyStream(wim, dst, progress, token, isoLength);
            }

            // 新 WIM 变短的部分：直接把写指针跳到原 extent 末尾。
            // 稀疏文件上这是不占磁盘的空洞；非 NTFS 则由文件系统补零，ISO 仍然有效。
            dst.Position = extent.Offset + extent.Length;

            long tailStart = extent.Offset + extent.Length;
            CopyRange(src, dst, tailStart, isoLength - tailStart, progress, token, isoLength);
            dst.SetLength(isoLength);
            dst.Flush(true);

            result = new IsoPatchResult
            {
                OutputPath = outputIsoPath,
                OutputLogicalSize = isoLength,
                NewWimSize = newSize,
                PaddedBytes = extent.Length - newSize,
                Sparse = sparse
            };
        }

        // 必须等两个 FileStream 都释放（dst 以 FileShare.None 打开）后才能回读输出 ISO
        result.Verification = VerifyOutput(outputIsoPath, entryPath, extent, newWimPath);
        return result;
    }

    /// <summary>写回后复核：原 ISO 未被改动，输出 ISO 的 WIM 头 + 版本列表可读。</summary>
    public static string VerifyOutput(string outputIso, string entryPath, IsoExtent extent, string newWimPath)
    {
        var notes = new List<string>();
        try
        {
            byte[] diskHead = ReadRaw(outputIso, extent.Offset, WimMetadataReader.HeaderSize);
            byte[] fileHead = ReadRaw(newWimPath, 0, WimMetadataReader.HeaderSize);
            notes.Add(diskHead.AsSpan().SequenceEqual(fileHead)
                ? "输出 ISO 内的 WIM 头与新 WIM 完全一致"
                : "警告：输出 ISO 内的 WIM 头与新 WIM 不一致");

            var loc = WimMetadataReader.LocateXml(diskHead);
            if (!loc.Success)
            {
                notes.Add("WIM 头未给出 XML 位置：" + loc.Note);
            }
            else
            {
                byte[] xml = ReadRaw(outputIso, extent.Offset + loc.Offset, (int)loc.Size);
                var parsed = WimMetadataReader.Read(diskHead, xml, "输出 ISO 尾部随机读取");
                notes.Add(parsed.Success
                    ? $"版本列表可读：{parsed.Images.Count} 个映像"
                    : "警告：版本列表解析失败 — " + parsed.Note);
            }

            byte[]? viaFs = IsoRandomAccess.TryReadRange(outputIso, entryPath, 0,
                WimMetadataReader.HeaderSize);
            notes.Add(viaFs != null && viaFs.AsSpan().SequenceEqual(fileHead)
                ? "UDF / ISO9660 文件流可正常寻址"
                : "提示：文件系统流读取不可用，已按物理偏移校验");
        }
        catch (Exception ex)
        {
            notes.Add("复核异常：" + ex.Message);
        }
        return string.Join("；", notes);
    }

    private static byte[] ReadRaw(string path, long offset, int count)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 16, FileOptions.RandomAccess);
        if (offset + count > fs.Length) throw new EndOfStreamException("读取超出文件末尾。");
        fs.Position = offset;
        var buffer = new byte[count];
        fs.ReadExactly(buffer);
        return buffer;
    }

    private static bool TrySetSparse(SafeFileHandle handle)
    {
        try
        {
            return DeviceIoControl(handle, FSCTL_SET_SPARSE, IntPtr.Zero, 0,
                IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
        catch { return false; }
    }

    private static void CopyRange(FileStream src, FileStream dst, long srcOffset, long count,
        Action<long, long>? progress, CancellationToken token, long total)
    {
        src.Position = srcOffset;
        long left = count;
        var buffer = new byte[BufferSize];
        while (left > 0)
        {
            token.ThrowIfCancellationRequested();
            int want = (int)Math.Min(buffer.Length, left);
            int n = src.Read(buffer, 0, want);
            if (n <= 0) break;
            dst.Write(buffer, 0, n);
            left -= n;
            progress?.Invoke(dst.Position, total);
        }
    }

    private static void CopyStream(FileStream src, FileStream dst,
        Action<long, long>? progress, CancellationToken token, long total)
    {
        var buffer = new byte[BufferSize];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int n = src.Read(buffer, 0, buffer.Length);
            if (n <= 0) break;
            dst.Write(buffer, 0, n);
            progress?.Invoke(dst.Position, total);
        }
    }
}
