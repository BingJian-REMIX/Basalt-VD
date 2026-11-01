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
}
