using System.Diagnostics;
using System.IO;
using Basalt.Common;
using Basalt.Models;
using Basalt.ViewModels;

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
            list.Add(new FolderNode
            {
                Name = Path.GetFileName(dir),
                FullPath = dir,
                IconSource = IconProvider.FolderIcon
            });
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
                ModifiedText = FileItemFormatter.FormatTime(fi.LastWriteTime),
                CreatedText = FileItemFormatter.FormatTime(fi.CreationTime),
                AttributesText = fi.Attributes.ToString(),
                IconSource = IconProvider.FromRealPath(file),
                IsDirectory = false
            });
        }
        return list;
    }

    public void ExtractFile(string path, string destPath)
        => File.Copy(path, destPath, overwrite: true);

    public string? GetParentPath(string path)
    {
        try
        {
            // 盘符根（如 "Z:\"）没有上级
            var parent = Directory.GetParent(path);
            return parent?.FullName;
        }
        catch
        {
            return null;
        }
    }

    public FileMetadata GetMetadata(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var di = new DirectoryInfo(path);
                return new FileMetadata
                {
                    Name = di.Name,
                    FullPath = di.FullName,
                    SizeText = "",
                    CreatedText = FileItemFormatter.FormatTime(di.CreationTime),
                    ModifiedText = FileItemFormatter.FormatTime(di.LastWriteTime),
                    AccessedText = FileItemFormatter.FormatTime(di.LastAccessTime),
                    AttributesText = di.Attributes.ToString(),
                    IsDirectory = true
                };
            }

            var fi = new FileInfo(path);
            return new FileMetadata
            {
                Name = fi.Name,
                FullPath = fi.FullName,
                SizeText = FileItemFormatter.FormatSize(fi.Length),
                Size = fi.Length,
                CreatedText = FileItemFormatter.FormatTime(fi.CreationTime),
                ModifiedText = FileItemFormatter.FormatTime(fi.LastWriteTime),
                AccessedText = FileItemFormatter.FormatTime(fi.LastAccessTime),
                AttributesText = fi.Attributes.ToString(),
                IsDirectory = false
            };
        }
        catch (Exception ex)
        {
            return new FileMetadata { Name = Path.GetFileName(path), FullPath = path, AttributesText = $"读取失败：{ex.Message}" };
        }
    }

    public System.Windows.Media.ImageSource? GetIcon(string path, bool isDirectory)
        => isDirectory ? IconProvider.FolderIcon : IconProvider.FromRealPath(path);

    public void OpenWithDefaultApp(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("文件不存在或已被移动。", path);

        // UseShellExecute=true 才能让系统按扩展名选择默认程序（zip/docx 等）
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public List<BreadcrumbItem> BuildBreadcrumb(string path)
    {
        var list = new List<BreadcrumbItem>();
        if (string.IsNullOrEmpty(path)) return list;

        // "Z:\Users\Admin" → ["Z:\" , "Users" , "Admin"]
        string root = Path.GetPathRoot(path) ?? path;
        list.Add(new BreadcrumbItem { Display = root.TrimEnd('\\'), Path = root });

        string rest = path[root.Length..].Trim('\\');
        string acc = root;
        foreach (var seg in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            acc = Path.Combine(acc, seg);
            list.Add(new BreadcrumbItem { Display = seg, Path = acc });
        }

        // 始终标记最后一项：它既是"当前级"，也是最后一级（不显示尾部分隔符）
        if (list.Count > 0)
        {
            var last = list[^1];
            list[^1] = new BreadcrumbItem { Display = last.Display, Path = last.Path, IsCurrent = true, IsLast = true };
        }
        return list;
    }

    public void Dispose() => _mount.Dispose();

    /// <summary>VHD 里的文件本来就在真实磁盘上，直接返回原路径。</summary>
    public string MaterializeToTemp(string path) => path;
}
