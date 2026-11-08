using System.Diagnostics;
using System.IO;
using DiscUtils;
using DiscUtils.Ntfs;
using DiscUtils.Partitions;
using DiscUtils.Setup;
using DiscUtils.Streams;

namespace Basalt.Services;

/// <summary>
/// 压缩包 → 真 VHDX（纯托管）。
/// <para>
/// VHDX 是块设备镜像，不能把文件流"直接封装"进去：必须依次建立
/// 动态 VHDX 容器 → MBR 分区表 → NTFS 文件系统，才能往里写文件。
/// 流程：7z x 解压到临时目录 → DiscUtils.Vhdx.Disk.InitializeDynamic 建盘 →
/// BiosPartitionTable 建分区 → NtfsFileSystem.Format 格式化 → 逐文件写入。
/// 全程不需要管理员权限，也不调用系统 format。
/// </para>
/// </summary>
public static class ArchiveToVhdxService
{
    private const long MinCapacity = 256L * 1024 * 1024; // NTFS 元数据需要空间，实测 256 MiB 可稳定格式化
    private const long Alignment = 8L * 1024 * 1024;

    static ArchiveToVhdxService()
    {
        // LTRData fork 不会自动注册提供器；Vhdx / 分区 / NTFS 都要显式登记。
        SetupHelper.RegisterAssembly(typeof(VirtualDisk).Assembly);
        SetupHelper.RegisterAssembly(typeof(DiscUtils.Vhdx.Disk).Assembly);
        SetupHelper.RegisterAssembly(typeof(NtfsFileSystem).Assembly);
    }

    /// <summary>把归档内容写成可挂载的 .vhdx，返回摘要文案。</summary>
    public static string Build(string archivePath, string outputPath)
    {
        string? sevenZip = ContainerDetector.SevenZipPath
            ?? throw new InvalidOperationException("未找到 7-Zip（7z.exe）。");

        string stage = Path.Combine(Path.GetTempPath(), $"BasaltVhdx_{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        try
        {
            ExtractAll(sevenZip, archivePath, stage);

            var files = Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories).ToList();
            if (files.Count == 0)
                throw new InvalidDataException("归档内没有可写入的文件。");

            long total = files.Sum(f => new FileInfo(f).Length);
            long capacity = Math.Max(MinCapacity, (long)(total * 1.4) + 64L * 1024 * 1024);
            capacity = ((capacity + Alignment - 1) / Alignment) * Alignment;

            using (var vhdxStream = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var disk = DiscUtils.Vhdx.Disk.InitializeDynamic(vhdxStream, Ownership.None, capacity))
            {
                var table = BiosPartitionTable.Initialize(disk);
                _ = table.CreateAligned(WellKnownPartitionType.WindowsNtfs, true, 1024 * 1024);

                var manager = new VolumeManager(disk);
                var logicals = manager.GetLogicalVolumes();
                if (logicals.Length == 0)
                    throw new InvalidDataException("未能在新建 VHDX 中识别到逻辑卷。");
                var volume = manager.GetVolume(logicals[0].Identity);

                using var fs = NtfsFileSystem.Format(volume, "BASALT");
                foreach (var file in files)
                {
                    string relative = Path.GetRelativePath(stage, file);
                    EnsureDirectories(fs, relative);
                    using var src = File.OpenRead(file);
                    using var dst = fs.OpenFile("\\" + relative.Replace('/', '\\'), FileMode.Create, FileAccess.Write);
                    src.CopyTo(dst);
                }
            }

            long physical = new FileInfo(outputPath).Length;
            return $"已生成 {Path.GetFileName(outputPath)} — {files.Count} 个文件 / " +
                   $"{Models.FileItemFormatter.FormatSize(total)}，虚拟容量 {Models.FileItemFormatter.FormatSize(capacity)}，" +
                   $"实际占用 {Models.FileItemFormatter.FormatSize(physical)}（NTFS 元数据预分配）";
        }
        finally
        {
            try { Directory.Delete(stage, recursive: true); } catch { /* 占用时留给系统清理 */ }
        }
    }

    /// <summary>回读校验：重新打开 VHDX，返回其中全部文件路径。</summary>
    public static IReadOnlyList<string> ListAllFiles(string vhdxPath)
    {
        using var disk = VirtualDisk.OpenDisk(vhdxPath, FileAccess.Read);
        using var partition = disk.Partitions[0].Open();
        var detected = FileSystemManager.DetectFileSystems(partition).ToList();
        if (detected.Count == 0) throw new InvalidDataException("VHDX 分区内未检测到文件系统。");
        partition.Position = 0;
        using var fs = detected[0].Open(partition, new FileSystemParameters());
        return fs.GetFiles("\\", "*.*", SearchOption.AllDirectories).ToList();
    }

    private static void ExtractAll(string sevenZip, string archivePath, string stage)
    {
        var psi = new ProcessStartInfo(sevenZip)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("x");               // 保留归档内目录层级
        psi.ArgumentList.Add(archivePath);
        psi.ArgumentList.Add("-o" + stage);
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-sccUTF-8");
        psi.ArgumentList.Add("-bso0");
        psi.ArgumentList.Add("-bsp0");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("无法启动 7-Zip。");
        string stderr = proc.StandardError.ReadToEnd();
        proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(300000);

        if (proc.ExitCode != 0)
            throw new InvalidDataException($"7-Zip 解压失败：{(stderr.Trim().Length > 0 ? stderr.Trim() : "非零退出码")}");
    }

    private static void EnsureDirectories(DiscFileSystem fs, string relative)
    {
        var parts = relative.Replace('/', '\\').Split('\\');
        string acc = "";
        for (int i = 0; i < parts.Length - 1; i++)
        {
            acc += "\\" + parts[i];
            if (!fs.DirectoryExists(acc)) fs.CreateDirectory(acc);
        }
    }
}
