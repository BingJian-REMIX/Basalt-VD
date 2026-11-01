using System.IO;
using Basalt.Models;

namespace Basalt.Services;

/// <summary>引擎工厂：按扩展名选择系统挂载（VHD/VHDX）或纯内存解析（VMDK）。</summary>
public static class DiskEngineFactory
{
    public static bool IsVmdk(string path) =>
        string.Equals(Path.GetExtension(path), ".vmdk", StringComparison.OrdinalIgnoreCase);

    /// <summary>创建对应引擎。VHD 走 virtdisk 系统挂载；VMDK 走 DiscUtils 内存解析（无盘符）。</summary>
    public static IVirtualDisk Create(string path, bool readOnly) =>
        IsVmdk(path) ? new VmdkDiskService(path) : new VhdDiskService(path, readOnly);
}
