using System.IO;
using Basalt.Models;

namespace Basalt.Services;

/// <summary>引擎工厂：按文件类型选择引擎。</summary>
public static class DiskEngineFactory
{
    public static bool IsVmdk(string path) =>
        string.Equals(Path.GetExtension(path), ".vmdk", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否是容器文件（zip/iso/rar/7z/SFX…）—— 走 7-Zip 展开浏览。</summary>
    public static bool IsContainer(string path) => ContainerDetector.IsContainerFile(path);

    /// <summary>
    /// 创建对应引擎。
    /// VHD → virtdisk 系统挂载（有盘符）；
    /// VMDK → DiscUtils 内存解析（无盘符）；
    /// 容器（zip/iso/rar/7z/SFX）→ 7-Zip 展开浏览（无盘符）。
    /// </summary>
    public static IVirtualDisk Create(string path, bool readOnly)
    {
        if (IsContainer(path)) return new ContainerDiskService(path);
        return IsVmdk(path) ? new VmdkDiskService(path) : new VhdDiskService(path, readOnly);
    }
}
