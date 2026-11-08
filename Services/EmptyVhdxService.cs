using System.IO;
using DiscUtils;
using DiscUtils.Ntfs;
using DiscUtils.Partitions;
using DiscUtils.Setup;
using DiscUtils.Streams;
using DiscUtils.Vhdx;

namespace Basalt.Services;

/// <summary>新建虚拟磁盘的分区表样式。</summary>
public enum PartitionStyle
{
    Mbr,
    Gpt
}

/// <summary>
/// 生成带一个空白 NTFS 分区的 VHDX（纯托管，不需要管理员权限、不调用系统 format）。
/// 链路：动态 VHDX 容器 → 分区表（MBR 或 GPT）→ 单个 NTFS 数据分区 → 格式化。
/// </summary>
public static class EmptyVhdxService
{
    public const long MinCapacityBytes = 1L * 1024 * 1024 * 1024;   // 1 GiB，NTFS 元数据需要空间
    public const long MaxCapacityBytes = 2048L * 1024 * 1024 * 1024; // 2 TiB

    static EmptyVhdxService()
    {
        // LTRData fork 不会自动注册提供器
        SetupHelper.RegisterAssembly(typeof(VirtualDisk).Assembly);
        SetupHelper.RegisterAssembly(typeof(Disk).Assembly);
        SetupHelper.RegisterAssembly(typeof(NtfsFileSystem).Assembly);
    }

    /// <summary>创建空白 VHDX，返回给用户的摘要文案。</summary>
    public static string Create(string outputPath, string diskName, int capacityGb, PartitionStyle style)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("保存路径不能为空。", nameof(outputPath));
        if (!outputPath.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("保存路径必须以 .vhdx 结尾。", nameof(outputPath));
        if (capacityGb < 1 || capacityGb > 2048)
            throw new ArgumentOutOfRangeException(nameof(capacityGb), "容量必须在 1 – 2048 GB 之间。");

        string? dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            throw new DirectoryNotFoundException($"保存目录不存在：{dir}");

        // NTFS 卷标上限 32 个字符
        string label = string.IsNullOrWhiteSpace(diskName) ? "BASALT" : diskName.Trim();
        if (label.Length > 32) label = label[..32];

        long capacity = capacityGb * 1024L * 1024 * 1024;
        using var vhdxStream = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var disk = Disk.InitializeDynamic(vhdxStream, Ownership.None, capacity);

        // GPT 用 GuidPartitionTable（Windows Basic Data），MBR 用 BiosPartitionTable（type 07）
        PartitionTable table = style == PartitionStyle.Gpt
            ? GuidPartitionTable.Initialize(disk)
            : BiosPartitionTable.Initialize(disk);
        _ = table.CreateAligned(WellKnownPartitionType.WindowsNtfs, true, 1024 * 1024);

        var manager = new VolumeManager(disk);
        var logicals = manager.GetLogicalVolumes();
        if (logicals.Length == 0)
            throw new InvalidDataException("未能在新建 VHDX 中识别到逻辑卷。");
        var volume = manager.GetVolume(logicals[0].Identity);

        using (NtfsFileSystem.Format(volume, label)) { }   // 空白分区：格式化后不写入任何文件

        long physical = new FileInfo(outputPath).Length;
        string styleName = style == PartitionStyle.Gpt ? "GPT" : "MBR";
        return $"已创建 {Path.GetFileName(outputPath)} — {styleName} 分区表 / 空白 NTFS / " +
               $"卷标「{label}」/ 虚拟容量 {capacityGb} GB，" +
               $"实际占用 {Models.FileItemFormatter.FormatSize(physical)}（动态分配，NTFS 元数据预分配）";
    }

    /// <summary>回读校验：返回分区样式、文件系统名与卷标。供自测与排错使用。</summary>
    public static (string PartitionStyleName, string FileSystem, string Label) Inspect(string vhdxPath)
    {
        using var disk = VirtualDisk.OpenDisk(vhdxPath, FileAccess.Read);
        string style = disk.Partitions.Count > 0 && disk.Partitions[0] is GuidPartitionInfo ? "GPT" : "MBR";
        using var partition = disk.Partitions[0].Open();
        var detected = FileSystemManager.DetectFileSystems(partition).ToList();
        if (detected.Count == 0) throw new InvalidDataException("VHDX 分区内未检测到文件系统。");
        partition.Position = 0;
        using var fs = detected[0].Open(partition, new FileSystemParameters());
        return (style, fs.FriendlyName, fs.VolumeLabel);
    }
}
