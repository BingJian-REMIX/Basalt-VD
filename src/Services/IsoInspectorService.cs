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

    /// <summary>XML 元数据在 WIM 内的绝对偏移（诊断用）。</summary>
    public string XmlOffsetText { get; set; } = "";
    /// <summary>随机读取失败时的原因（诊断用）。</summary>
    public string? XmlReadNote { get; set; }

    public string SourcesDisplay => SourcesPath ?? "未找到 sources 目录（已列出镜像根）";
    public string InstallImageDisplay => InstallImageName ?? "未找到 install.wim / install.esd";
    public string InstallImageSizeText => InstallImagePath == null
        ? "—"
        : FileItemFormatter.FormatSize(InstallImageSize);
}

/// <summary>
/// ISO 检查器的数据层：定位 sources → 找到 install.wim / install.esd → 读大小 → 解析 WIM XML 版本列表。
/// 只依赖 IVirtualDisk，因此容器内嵌 ISO、真实路径 ISO 都能复用。
/// <para>
/// 真实 Windows ISO 的 install.wim 有 4~6 GB 且 XML 在文件尾部，采用两阶段读取：
/// 先 ReadHead(208) 定位 XML 绝对偏移，再 ReadRange 精确取回那 16 KB，绝不把整个映像灌进内存。
/// </para>
/// </summary>
public static class IsoInspectorService
{
    /// <summary>第一阶段只读 WIM 头。XML 位置由头里的 rhXmlData 给出，不必预读整个开头。</summary>
    public const int HeaderBytes = WimMetadataReader.HeaderSize;

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

    public static IsoInspectionResult Inspect(IVirtualDisk disk)
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
            byte[] head = disk.ReadHead(install.FullPath, HeaderBytes);
            var loc = WimMetadataReader.LocateXml(head);
            result.XmlOffsetText = loc.Success ? $"{loc.Offset} 字节（{loc.OffsetText}）" : "不可用";

            byte[]? xml = null;
            string source = "";
            if (loc.Success)
            {
                if (loc.Offset + loc.Size <= head.Length)
                {
                    source = "头部预读";
                }
                else
                {
                    // XML 在 GB 级映像的尾部：按绝对偏移随机读取（ISO 走 UDF seek，毫秒级）
                    try
                    {
                        xml = disk.ReadRange(install.FullPath, loc.Offset, (int)loc.Size);
                        if (xml.Length == 0)
                        {
                            xml = null;
                            result.XmlReadNote = "按偏移读取未返回数据";
                        }
                        else
                        {
                            source = $"尾部随机读取 @{loc.OffsetText}";
                        }
                    }
                    catch (Exception ex)
                    {
                        result.XmlReadNote = ex.Message;
                    }
                }
            }

            result.Wim = WimMetadataReader.Read(head, xml, source);
            result.Images.AddRange(result.Wim.Images);
            if (!result.Wim.Success)
                result.Error = "版本列表不可用，已降级为文件清单；可先提取安装映像到本地。" +
                               (result.XmlReadNote != null ? $"（{result.XmlReadNote}）" : "");
        }
        catch (Exception ex)
        {
            result.Error = $"读取安装映像头失败：{ex.Message} — 已降级为文件清单，可直接提取。";
        }

        return result;
    }
}
