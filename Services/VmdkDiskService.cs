using System.Diagnostics;
using System.IO;
using Basalt.Common;
using Basalt.Models;
using Basalt.ViewModels;
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
                    FullPath = i.ToString(),
                    IconSource = IconProvider.FolderIcon
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
                FullPath = $"{idx}{dir}", // 保持 "0\DIR\SUB" 伪路径
                IconSource = IconProvider.FolderIcon
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
            string ext = Path.GetExtension(file);
            list.Add(new FileItem
            {
                Name = Path.GetFileName(file),
                FullPath = $"{idx}{file}",
                SizeText = FileItemFormatter.FormatSize(fs.GetFileLength(file)),
                ModifiedText = FileItemFormatter.FormatTime(fs.GetLastWriteTimeUtc(file).ToLocalTime()),
                CreatedText = FileItemFormatter.FormatTime(fs.GetCreationTimeUtc(file).ToLocalTime()),
                AttributesText = SafeAttributes(fs, file),
                // VMDK 内文件无真实路径，只能按扩展名取系统关联图标
                IconSource = IconProvider.FromExtension(ext),
                IsDirectory = false
            });
        }
        return list;
    }

    private static string SafeAttributes(DiscFileSystem fs, string path)
    {
        try { return fs.GetAttributes(path).ToString(); }
        catch { return ""; }
    }

    public void ExtractFile(string path, string destPath)
    {
        if (!TryGetFileSystem(path, out var fs, out _, out string inner))
            throw new FileNotFoundException("找不到 VMDK 内的文件。", path);

        using var src = fs.OpenFile(inner, FileMode.Open, FileAccess.Read);
        using var dst = File.Create(destPath);
        src.CopyTo(dst);
    }

    public string? GetParentPath(string path)
    {
        // 镜像根（空串）没有上级
        if (string.IsNullOrEmpty(path)) return null;
        int sep = path.LastIndexOf('\\');
        // 分区根（"0"）的上一级是镜像根（空串）—— 回到镜像根可以重新选分区
        if (sep < 0) return "";
        return path[..sep];
    }

    public FileMetadata GetMetadata(string path)
    {
        if (!TryGetFileSystem(path, out var fs, out int idx, out string inner))
            return new FileMetadata { Name = Path.GetFileName(path), FullPath = path };

        try
        {
            if (fs.DirectoryExists(inner))
            {
                return new FileMetadata
                {
                    Name = Path.GetFileName(inner.TrimEnd('\\')),
                    FullPath = $"{idx}{inner}",
                    CreatedText = FileItemFormatter.FormatTime(fs.GetCreationTimeUtc(inner).ToLocalTime()),
                    ModifiedText = FileItemFormatter.FormatTime(fs.GetLastWriteTimeUtc(inner).ToLocalTime()),
                    AccessedText = FileItemFormatter.FormatTime(fs.GetLastAccessTimeUtc(inner).ToLocalTime()),
                    AttributesText = SafeAttributes(fs, inner),
                    IsDirectory = true
                };
            }

            long length = fs.GetFileLength(inner);
            return new FileMetadata
            {
                Name = Path.GetFileName(inner),
                FullPath = $"{idx}{inner}",
                SizeText = FileItemFormatter.FormatSize(length),
                Size = length,
                CreatedText = FileItemFormatter.FormatTime(fs.GetCreationTimeUtc(inner).ToLocalTime()),
                ModifiedText = FileItemFormatter.FormatTime(fs.GetLastWriteTimeUtc(inner).ToLocalTime()),
                AccessedText = FileItemFormatter.FormatTime(fs.GetLastAccessTimeUtc(inner).ToLocalTime()),
                AttributesText = SafeAttributes(fs, inner),
                IsDirectory = false
            };
        }
        catch (Exception ex)
        {
            return new FileMetadata { Name = Path.GetFileName(inner), FullPath = $"{idx}{inner}", AttributesText = $"读取失败：{ex.Message}" };
        }
    }

    public System.Windows.Media.ImageSource? GetIcon(string path, bool isDirectory)
    {
        if (isDirectory) return IconProvider.FolderIcon;
        if (!TryGetFileSystem(path, out var fs, out _, out string inner)) return null;
        return IconProvider.FromExtension(Path.GetExtension(inner));
    }

    public void OpenWithDefaultApp(string path)
    {
        if (!TryGetFileSystem(path, out var fs, out _, out string inner))
            throw new FileNotFoundException("找不到 VMDK 内的文件。", path);

        // VMDK 内文件不存在于真实磁盘，无法直接交给 shell —— 先导出到临时文件再打开。
        string ext = Path.GetExtension(inner);
        string temp = Path.Combine(Path.GetTempPath(), $"basalt_open_{Guid.NewGuid():N}{ext}");
        try
        {
            using (var src = fs.OpenFile(inner, FileMode.Open, FileAccess.Read))
            using (var dst = File.Create(temp))
            {
                src.CopyTo(dst);
            }
            Process.Start(new ProcessStartInfo(temp) { UseShellExecute = true });
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            throw;
        }
    }

    public List<BreadcrumbItem> BuildBreadcrumb(string path)
    {
        var list = new List<BreadcrumbItem>();
        list.Add(new BreadcrumbItem { Display = DisplayName, Path = "" });

        if (string.IsNullOrEmpty(path)) return list;

        // "0\Users\Admin" → 镜像 > 分区 N > Users > Admin
        int sep = path.IndexOf('\\');
        string indexText = sep < 0 ? path : path[..sep];
        if (!int.TryParse(indexText, out int idx) || idx < 0 || idx >= _fileSystems.Count)
            return list;

        string label = _fileSystems[idx] != null
            ? $"分区 {idx + 1}（{_fileSystems[idx]!.FriendlyName}）"
            : $"分区 {idx + 1}（{_partitionTypes[idx]}）";
        list.Add(new BreadcrumbItem { Display = label, Path = indexText });

        if (sep >= 0)
        {
            string rest = path[(sep + 1)..];
            string acc = indexText;
            foreach (var seg in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                acc += "\\" + seg;
                list.Add(new BreadcrumbItem { Display = seg, Path = acc });
            }
        }

        if (list.Count > 0)
            list[^1] = new BreadcrumbItem { Display = list[^1].Display, Path = list[^1].Path, IsCurrent = true, IsLast = true };
        return list;
    }

    public void Dispose()
    {
        foreach (var fs in _fileSystems) fs?.Dispose();
        _disk.Dispose();
    }
}
