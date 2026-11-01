using System.IO;
using Basalt.Models;

namespace Basalt.Services;

/// <summary>VHD/VHDX 引擎：virtdisk.dll 系统挂载 + System.IO 读取盘符。</summary>
public sealed class VhdDiskService : IVirtualDisk
{
    private readonly MountService _mount = new();

    public string DisplayName { get; }
    public char? DriveLetter => _mount.MountedDriveLetter;
    public bool MountedReadOnly => _mount.MountedReadOnly;

    /// <summary>构造即挂载；失败抛异常。</summary>
    public VhdDiskService(string path, bool readOnly)
    {
        char letter = _mount.MountVHD(path, readOnly);
        DisplayName = $"{letter}:\\";
    }

    public List<FolderNode> GetDirectories(string path)
    {
        var list = new List<FolderNode>();
        foreach (var dir in Directory.EnumerateDirectories(path))
        {
            list.Add(new FolderNode { Name = Path.GetFileName(dir), FullPath = dir });
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    public List<FileItem> GetFiles(string path)
    {
        var list = new List<FileItem>();
        foreach (var file in Directory.EnumerateFiles(path))
        {
            var fi = new FileInfo(file);
            list.Add(new FileItem
            {
                Name = fi.Name,
                FullPath = fi.FullName,
                SizeText = FileItemFormatter.FormatSize(fi.Length),
                ModifiedText = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                Icon = FileItemFormatter.GetIcon(fi.Extension)
            });
        }
        return list;
    }

    public void ExtractFile(string path, string destPath)
        => File.Copy(path, destPath, overwrite: true);

    public void Dispose() => _mount.Dispose();
}
