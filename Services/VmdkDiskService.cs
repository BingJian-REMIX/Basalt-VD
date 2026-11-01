using System.IO;
using Basalt.Models;
using DiscUtils;
using DiscUtils.Partitions;
using DiscUtils.Streams;
using DiscUtils.Vmdk;

namespace Basalt.Services;

/// <summary>
/// VMDK 引擎：纯软件级解析，不依赖系统挂载、不分配盘符。
/// DiscUtils 打开镜像 → 枚举分区（MBR/GPT）→ 识别文件系统（NTFS/FAT…）→ 内存中读写。
/// 伪路径："0" = 第 0 个可识别分区的根；"0\DIR\SUB\FILE" = 分区内路径。
/// </summary>
public sealed class VmdkDiskService : IVirtualDisk
{
    private readonly VirtualDisk _disk;
    private readonly List<PartitionInfo> _partitions = new();
    private readonly List<DiscFileSystem?> _fileSystems = new();
    private readonly List<string> _partitionTypes = new();

    /// <summary>无法读取文件系统的分区数（如 ext4 / Linux / 未格式化）。用于向用户说明。</summary>
    public int UnsupportedPartitionCount { get; private set; }

    /// <summary>当存在不可读分区时给出的说明（空字符串表示全部可读）。</summary>
    public string UnsupportedNote =>
        UnsupportedPartitionCount == 0 ? "" :
        $"{UnsupportedPartitionCount} 个分区因文件系统不受支持（如 ext4 / Linux）无法读取，仅显示分区结构。";

    static VmdkDiskService()
    {
        // LTRData fork：引用包不会自动注册提供器，必须显式注册。
        // SetupHelper.RegisterAssembly 会用反射扫描该程序集内的 [FileSystem]/[VirtualDisk]
        // 等特性，把 FAT/NTFS 文件系统、VMDK 虚拟磁盘类型、逻辑卷工厂都登记上。
        DiscUtils.Setup.SetupHelper.RegisterAssembly(typeof(DiscUtils.VirtualDisk).Assembly);        // DiscUtils.Core
        DiscUtils.Setup.SetupHelper.RegisterAssembly(typeof(DiscUtils.Vmdk.Disk).Assembly);         // DiscUtils.Vmdk
        DiscUtils.Setup.SetupHelper.RegisterAssembly(typeof(DiscUtils.Fat.FatFileSystem).Assembly);  // DiscUtils.Fat
        DiscUtils.Setup.SetupHelper.RegisterAssembly(typeof(DiscUtils.Ntfs.NtfsFileSystem).Assembly);// DiscUtils.Ntfs
    }

    public string DisplayName { get; }
    public char? DriveLetter => null;

    public VmdkDiskService(string path)
    {
        DisplayName = Path.GetFileName(path);
        // 必须用 OpenDisk(path,...) ：它会解析描述符并打开各分片（支持 Monolithic / Split / Streaming）。
        // 用 new Disk(stream,...) 仅支持单体盘，拆分 VMDK 会抛 "Only Monolithic Sparse ... via a stream"。
        _disk = Disk.OpenDisk(path, FileAccess.Read);

        var tables = PartitionTable.GetPartitionTables(_disk.Content);
        foreach (var table in tables)
        {
            foreach (var partition in table.Partitions)
            {
                _partitions.Add(partition);
                var fs = DetectFileSystem(partition);
                _fileSystems.Add(fs);
                _partitionTypes.Add(GetPartitionType(partition));
                if (fs == null) UnsupportedPartitionCount++;
            }
        }

        // 注意：即使“没有可读文件系统”也不再整体失败 —— 分区可能真实存在（如 ext4 / Linux），
        // 只是当前 .NET 引擎（DiscUtils/LTRData）不支持读取。这类分区会照常列出并标注，便于用户理解。
        // 仅当磁盘完全没有分区时才报错。
        if (_partitions.Count == 0)
            throw new InvalidDataException("VMDK 中未找到任何分区（磁盘可能为空或未分区）。");
    }

    private static string GetPartitionType(PartitionInfo partition)
    {
        try { return partition.TypeAsString ?? "未知类型"; }
        catch { return "未知类型"; }
    }

    private static DiscFileSystem? DetectFileSystem(PartitionInfo partition)
    {
        try
        {
            using var stream = partition.Open();
            var fsInfos = FileSystemManager.DetectFileSystems(stream).ToList();
            if (fsInfos.Count == 0) return null;
            stream.Position = 0;
            // 必须传非 null 的 FileSystemParameters：LTRData 的 FAT 实现在参数为 null 时
            // 其内部 FatFileSystemOptions 构造会 NullReferenceException（NTFS 则无此问题）。
            // 统一传入一个空参数对象即可规避该库缺陷，同时兼容 NTFS。
            return fsInfos[0].Open(stream, new FileSystemParameters());
        }
        catch
        {
            return null; // 未格式化/RAW 分区，或文件系统不受支持（如 ext4/Linux），跳过
        }
    }

    // 伪路径拆分："0\DIR\FILE" → 分区 0 + 文件系统内路径 "\DIR\FILE"
    private bool TryGetFileSystem(string path, out DiscFileSystem fs, out int partitionIndex, out string innerPath)
    {
        fs = null!;
        partitionIndex = 0;
        innerPath = "\\";
        if (string.IsNullOrEmpty(path)) return false;

        int sep = path.IndexOf('\\');
        string indexText = sep < 0 ? path : path[..sep];
        if (!int.TryParse(indexText, out int idx) || idx < 0 || idx >= _fileSystems.Count)
            return false;

        var candidate = _fileSystems[idx];
        if (candidate == null) return false;

        fs = candidate;
        partitionIndex = idx;
        if (sep >= 0)
        {
            innerPath = path[(sep + 1)..];
            if (!innerPath.StartsWith('\\')) innerPath = "\\" + innerPath;
        }
        return true;
    }

    public List<FolderNode> GetDirectories(string path)
    {
        var list = new List<FolderNode>();

        // 根节点：列出所有分区。能识别文件系统的正常显示；
        // 不能识别的（如 ext4 / Linux / 未格式化）也列出并标注，便于用户理解“为什么读不到文件”。
        if (string.IsNullOrEmpty(path))
        {
            for (int i = 0; i < _fileSystems.Count; i++)
            {
                string label = _fileSystems[i] != null
                    ? $"分区 {i + 1}（{_fileSystems[i]!.FriendlyName}）"
                    : $"分区 {i + 1}（{_partitionTypes[i]} — 暂不支持读取）";
                list.Add(new FolderNode
                {
                    Name = label,
                    FullPath = i.ToString()
                });
            }
            return list;
        }

        if (!TryGetFileSystem(path, out var fs, out int idx, out string inner))
            return list;

        foreach (var dir in fs.GetDirectories(inner))
        {
            list.Add(new FolderNode
            {
                Name = Path.GetFileName(dir),
                FullPath = $"{idx}{dir}" // 保持 "0\DIR\SUB" 伪路径
            });
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    public List<FileItem> GetFiles(string path)
    {
        var list = new List<FileItem>();
        if (string.IsNullOrEmpty(path)) return list; // 镜像根下只有分区
        if (!TryGetFileSystem(path, out var fs, out int idx, out string inner))
            return list;

        foreach (var file in fs.GetFiles(inner))
        {
            list.Add(new FileItem
            {
                Name = Path.GetFileName(file),
                FullPath = $"{idx}{file}",
                SizeText = FileItemFormatter.FormatSize(fs.GetFileLength(file)),
                ModifiedText = fs.GetLastWriteTimeUtc(file).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                Icon = FileItemFormatter.GetIcon(Path.GetExtension(file))
            });
        }
        return list;
    }

    public void ExtractFile(string path, string destPath)
    {
        if (!TryGetFileSystem(path, out var fs, out _, out string inner))
            throw new FileNotFoundException("找不到 VMDK 内的文件。", path);

        using var src = fs.OpenFile(inner, FileMode.Open, FileAccess.Read);
        using var dst = File.Create(destPath);
        src.CopyTo(dst);
    }

    public void Dispose()
    {
        foreach (var fs in _fileSystems) fs?.Dispose();
        _disk.Dispose();
    }
}
