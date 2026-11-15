using System.IO;
using Basalt.Models;

namespace Basalt.Services;

/// <summary>sources 目录里的一个文件。</summary>
public sealed class SourceFileInfo
{
    public string Name { get; init; } = "";
    public string SizeText { get; init; } = "";
    public bool IsInstallImage { get; set; }
}

/// <summary>ISO 检查结果。任何一步失败都只写 Error / Note，不抛异常给 UI。</summary>
public sealed class IsoInspectionResult
{
    public string? SourcesPath { get; set; }
    public string? InstallImagePath { get; set; }
    public string? InstallImageName { get; set; }
    public long InstallImageSize { get; set; }
    public List<SourceFileInfo> Files { get; } = new();
    public List<WindowsImageInfo> Images { get; } = new();
    public WimReadResult? Wim { get; set; }
    public string? Error { get; set; }

    public string SourcesDisplay => SourcesPath ?? "未找到 sources 目录（已列出镜像根）";
    public string InstallImageDisplay => InstallImageName ?? "未找到 install.wim / install.esd";
    public string InstallImageSizeText => InstallImagePath == null
        ? "—"
        : FileItemFormatter.FormatSize(InstallImageSize);
}

/// <summary>
/// ISO 检查器的数据层：定位 sources → 找到 install.wim / install.esd → 读大小 → 解析 WIM XML 版本列表。
/// 只依赖 IVirtualDisk，因此容器内嵌 ISO、真实路径 ISO 都能复用。
/// </summary>
public static class IsoInspectorService
{
    /// <summary>预读上限：WIM 头 + XML 资源都在文件开头；避免把 4~6 GB 的 install.wim 整体载入内存。</summary>
    public const int PrefetchBytes = 16 * 1024 * 1024;

    /// <summary>在镜像根下找 sources 目录（大小写不敏感，ISO 里常写作 SOURCES）。找不到返回 null。</summary>
    public static string? FindSourcesPath(IVirtualDisk disk)
    {
        try
        {
            return disk.GetDirectories("")
                .FirstOrDefault(d => d.Name.Equals("sources", StringComparison.OrdinalIgnoreCase))
                ?.FullPath;
        }
        catch
        {
            return null;
        }
    }

    public static IsoInspectionResult Inspect(IVirtualDisk disk, int prefetchBytes = PrefetchBytes)
    {
        var result = new IsoInspectionResult();

        string? sources = FindSourcesPath(disk);
        result.SourcesPath = sources;

        List<FileItem> files;
        try
        {
            files = disk.GetFiles(sources ?? "").ToList();
        }
        catch (Exception ex)
        {
            result.Error = $"读取目录失败：{ex.Message}";
            return result;
        }

        foreach (var f in files)
            result.Files.Add(new SourceFileInfo { Name = f.Name, SizeText = f.SizeText });

        var install = files.FirstOrDefault(f => f.Name.Equals("install.wim", StringComparison.OrdinalIgnoreCase))
                      ?? files.FirstOrDefault(f => f.Name.Equals("install.esd", StringComparison.OrdinalIgnoreCase));

        if (install == null)
        {
            result.Error = "未找到 install.wim / install.esd — 这不是 Windows 安装镜像，或 sources 结构不同。";
            return result;
        }

        result.InstallImagePath = install.FullPath;
        result.InstallImageName = install.Name;
        result.InstallImageSize = install.Size;

        var hit = result.Files.FirstOrDefault(f => f.Name.Equals(install.Name, StringComparison.OrdinalIgnoreCase));
        if (hit != null) hit.IsInstallImage = true;

        try
        {
            byte[] head = disk.ReadHead(install.FullPath, prefetchBytes);
            result.Wim = WimMetadataReader.Read(head);
            result.Images.AddRange(result.Wim.Images);
            if (!result.Wim.Success)
                result.Error = "版本列表不可用，已降级为文件清单；可先提取安装映像到本地。";
        }
        catch (Exception ex)
        {
            result.Error = $"读取安装映像头失败：{ex.Message} — 已降级为文件清单，可直接提取。";
        }

        return result;
    }
}
