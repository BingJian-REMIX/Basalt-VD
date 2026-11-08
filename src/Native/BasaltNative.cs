using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Basalt.Native;

/// <summary>virtdisk.dll P/Invoke 封装（VHD / VHDX 挂载、卸载）。</summary>
public static class BasaltNative
{
    public const uint ERROR_SUCCESS = 0;

    public static readonly Guid VIRTUAL_STORAGE_TYPE_VENDOR_MICROSOFT =
        new("EC984AEC-A0F9-47e9-901F-71415A66345B");

    public const uint VIRTUAL_STORAGE_TYPE_DEVICE_VHD = 2;
    public const uint VIRTUAL_STORAGE_TYPE_DEVICE_VHDX = 3;

    public const uint VIRTUAL_DISK_ACCESS_NONE = 0x00000000;
    public const uint VIRTUAL_DISK_ACCESS_ATTACH_RO = 0x00010000;
    public const uint VIRTUAL_DISK_ACCESS_ATTACH_RW = 0x00020000;
    public const uint VIRTUAL_DISK_ACCESS_DETACH = 0x00040000;
    public const uint VIRTUAL_DISK_ACCESS_GET_INFO = 0x00080000;
    public const uint VIRTUAL_DISK_ACCESS_CREATE = 0x00100000;

    public const uint OPEN_VIRTUAL_DISK_FLAG_NONE = 0x0;
    public const uint ATTACH_VIRTUAL_DISK_FLAG_NONE = 0x0;
    public const uint ATTACH_VIRTUAL_DISK_FLAG_READ_ONLY = 0x1;
    public const uint ATTACH_VIRTUAL_DISK_FLAG_NO_DRIVE_LETTER = 0x2;
    public const uint DETACH_VIRTUAL_DISK_FLAG_NONE = 0x0;

    public const uint OPEN_VIRTUAL_DISK_VERSION_1 = 1;
    public const uint ATTACH_VIRTUAL_DISK_VERSION_1 = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct VIRTUAL_STORAGE_TYPE
    {
        public uint DeviceId;
        public Guid VendorId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OPEN_VIRTUAL_DISK_PARAMETERS
    {
        public uint Version;
        public uint RWDepth; // Version1：0=只读链；1(OPEN_VIRTUAL_DISK_RW_DEPTH_DEFAULT)=默认可读写
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ATTACH_VIRTUAL_DISK_PARAMETERS
    {
        public uint Version;
        public uint Reserved;
    }

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint OpenVirtualDisk(
        ref VIRTUAL_STORAGE_TYPE virtualDiskStorageType,
        string path,
        uint virtualDiskAccessMask,
        uint flags,
        ref OPEN_VIRTUAL_DISK_PARAMETERS parameters,
        out IntPtr handle);

    [DllImport("virtdisk.dll", SetLastError = true)]
    public static extern uint AttachVirtualDisk(
        IntPtr virtualDiskHandle,
        IntPtr securityDescriptor,
        uint flags,
        uint providerSpecificFlags,
        ref ATTACH_VIRTUAL_DISK_PARAMETERS parameters,
        IntPtr overlapped);

    [DllImport("virtdisk.dll", SetLastError = true)]
    public static extern uint DetachVirtualDisk(
        IntPtr virtualDiskHandle,
        uint flags,
        uint providerSpecificFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    /// <summary>获取已挂载虚拟磁盘对应的物理磁盘路径，如 "\\.\PhysicalDrive3"。</summary>
    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetVirtualDiskPhysicalPath(
        IntPtr virtualDiskHandle,
        ref uint physicalPathSizeInBytes,
        IntPtr physicalPath);

    // ---- CreateVirtualDisk（自检用）----

    public const uint CREATE_VIRTUAL_DISK_VERSION_1 = 1;
    public const uint CREATE_VIRTUAL_DISK_FLAGS_FULL_PHYSICAL_ALLOCATION = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct CREATE_VIRTUAL_DISK_PARAMETERS
    {
        public uint Version;
        public Guid UniqueId;
        public ulong MaximumSize;
        public uint BlockSizeInBytes;
        public uint SectorSizeInBytes;
        public IntPtr ParentPath;    // PCWSTR, null
        public IntPtr SourcePath;    // PCWSTR, null
    }

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint CreateVirtualDisk(
        ref VIRTUAL_STORAGE_TYPE virtualDiskStorageType,
        string path,
        uint virtualDiskAccessMask,
        IntPtr securityDescriptor,
        uint flags,
        uint providerSpecificFlags,
        ref CREATE_VIRTUAL_DISK_PARAMETERS parameters,
        IntPtr overlapped,
        out IntPtr handle);

    /// <summary>根据扩展名推断虚拟磁盘设备类型（VHD / VHDX）。</summary>
    public static uint GetDeviceId(string path)
    {
        var ext = Path.GetExtension(path);
        return string.Equals(ext, ".vhdx", StringComparison.OrdinalIgnoreCase)
            ? VIRTUAL_STORAGE_TYPE_DEVICE_VHDX
            : VIRTUAL_STORAGE_TYPE_DEVICE_VHD;
    }
}
