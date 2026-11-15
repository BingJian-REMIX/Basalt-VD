using System.IO;
using Basalt.Models;

namespace Basalt.Services;

/// <summary>
/// 双引擎统一的虚拟磁盘访问抽象。
/// path 约定：VHD 引擎为真实文件系统路径（如 Z:\DIR）；
/// VMDK 引擎为伪路径（如 "0" = 第 0 个分区根，"0\DIR\FILE" = 该分区内路径）。
/// </summary>
public interface IVirtualDisk : IDisposable
{
    /// <summary>显示名：VHD 为盘符路径，VMDK 为文件名。</summary>
    string DisplayName { get; }

    /// <summary>挂载到的盘符；纯内存解析（VMDK）时为 null。</summary>
    char? DriveLetter { get; }

    /// <summary>列出目录下的子目录（用于左侧 TreeView）。path 为空串表示镜像根（VMDK 返回分区节点）。</summary>
    List<FolderNode> GetDirectories(string path);

    /// <summary>列出目录下的文件（不含子目录）。</summary>
    List<FileItem> GetFiles(string path);

    /// <summary>把镜像内的文件提取到本地真实路径。</summary>
    void ExtractFile(string path, string destPath);

    /// <summary>
    /// 返回上级目录路径；已在最顶层时返回 null（列表不显示 ".."）。
    /// VHD: 真实父目录；VMDK: 伪路径的上一级（"0\A\B" → "0\A"，"0" → null）。
    /// </summary>
    string? GetParentPath(string path);

    /// <summary>读取单个文件/目录的完整元数据（属性窗口用）。</summary>
    FileMetadata GetMetadata(string path);

    /// <summary>
    /// 取该路径对应的原生 Windows 图标；取不到返回 null。
    /// VHD 用真实路径取；VMDK 按扩展名取关联图标。
    /// </summary>
    System.Windows.Media.ImageSource? GetIcon(string path, bool isDirectory);

    /// <summary>
    /// 打开文件：用系统默认程序。
    /// VHD：文件在真实磁盘，可直接交给 shell。
    /// VMDK：文件仅存在于镜像内，先导出到临时文件再交给 shell。
    /// </summary>
    void OpenWithDefaultApp(string path);

    /// <summary>
    /// 构造面包屑层级链（从根到当前）。每项含显示名与可跳转路径。
    /// </summary>
    List<ViewModels.BreadcrumbItem> BuildBreadcrumb(string path);

    /// <summary>
    /// 打开镜像内文件的只读流，供内部预览使用（不落地、不经过 Process.Start）。
    /// VHD 为 FileStream；VMDK 为 DiscUtils 文件系统流；容器为 7z -so 输出的纯内存流。
    /// 调用方负责 Dispose。
    /// </summary>
    Stream OpenRead(string path);

    /// <summary>
    /// 只预读文件开头 maxBytes 字节。用于 install.wim / install.esd 这类 GB 级文件 ——
    /// 直接走 OpenRead 会把整个映像载入内存。默认实现从返回的流里读够即止；
    /// 容器引擎重写为 7z -so 提前终止，避免 7-Zip 把 6 GB 全推到管道里。
    /// </summary>
    byte[] ReadHead(string path, int maxBytes)
    {
        using var s = OpenRead(path);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int left = maxBytes;
        while (left > 0)
        {
            int n = s.Read(chunk, 0, Math.Min(chunk.Length, left));
            if (n <= 0) break;
            buffer.Write(chunk, 0, n);
            left -= n;
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// 把镜像内的文件"实体化"到本地真实路径，返回该真实路径。
    /// 用于把归档文件交给外部程序（7-Zip）处理 —— 归档必须先落地成真实文件。
    /// VHD：路径本身就是真实的，直接返回；VMDK/容器：需先导出到临时目录。
    /// </summary>
    string MaterializeToTemp(string path);
}
