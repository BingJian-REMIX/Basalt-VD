using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Basalt.Native;

/// <summary>独立诊断：逐步执行 OpenVirtualDisk / AttachVirtualDisk，输出每步错误码。</summary>
public static class VhdDiag
{
    private static readonly Guid VendorMicrosoft = new("EC984AEC-A0F9-47e9-901F-71415A66345B");

    public static string Run(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"file      = {path}");
        sb.AppendLine($"exists    = {File.Exists(path)}");
        try { sb.AppendLine($"fileattrs = {File.GetAttributes(path)}"); } catch (Exception ex) { sb.AppendLine($"fileattrs = <error: {ex.Message}>"); }
        using (var id = WindowsIdentity.GetCurrent())
            sb.AppendLine($"elevated  = {new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator)}");
        sb.AppendLine();

        var storageType = new BasaltNative.VIRTUAL_STORAGE_TYPE
        {
            DeviceId = BasaltNative.GetDeviceId(path),
            VendorId = VendorMicrosoft
        };
        var openParams = new BasaltNative.OPEN_VIRTUAL_DISK_PARAMETERS
        {
            Version = BasaltNative.OPEN_VIRTUAL_DISK_VERSION_1,
            RWDepth = 1 // OPEN_VIRTUAL_DISK_RW_DEPTH_DEFAULT
        };

        uint r = BasaltNative.OpenVirtualDisk(
            ref storageType, path,
            BasaltNative.VIRTUAL_DISK_ACCESS_ATTACH_RW,
            BasaltNative.OPEN_VIRTUAL_DISK_FLAG_NONE,
            ref openParams, out IntPtr handle);
        sb.AppendLine($"OpenVirtualDisk          = 0x{r:X8}  ({ErrorName(r)})");
        if (r != 0) return sb.ToString();

        var attachParams = new BasaltNative.ATTACH_VIRTUAL_DISK_PARAMETERS { Version = 1 };
        r = BasaltNative.AttachVirtualDisk(handle, IntPtr.Zero, 0, 0, ref attachParams, IntPtr.Zero);
        sb.AppendLine($"AttachVirtualDisk(RW)    = 0x{r:X8}  ({ErrorName(r)})");

        if (r != 0)
        {
            r = BasaltNative.AttachVirtualDisk(handle, IntPtr.Zero, 2, 0, ref attachParams, IntPtr.Zero);
            sb.AppendLine($"AttachVirtualDisk(RO)    = 0x{r:X8}  ({ErrorName(r)})");
        }

        if (r == 0)
        {
            sb.AppendLine("RESULT: 挂载成功（诊断后已卸载）");
            BasaltNative.DetachVirtualDisk(handle, 0, 0);
        }
        BasaltNative.CloseHandle(handle);
        return sb.ToString();
    }

    /// <summary>对照实验：新建一个 32MB 固定 VHD → 挂载 → 卸载 → 删除，逐步输出错误码。</summary>
    public static string SelfTest()
    {
        var sb = new StringBuilder();
        string path = Path.Combine(Path.GetTempPath(), "basalt_selftest.vhd");
        try
        {
            if (File.Exists(path)) File.Delete(path);

            var storageType = new BasaltNative.VIRTUAL_STORAGE_TYPE
            {
                DeviceId = BasaltNative.VIRTUAL_STORAGE_TYPE_DEVICE_VHD,
                VendorId = VendorMicrosoft
            };
            var createParams = new BasaltNative.CREATE_VIRTUAL_DISK_PARAMETERS
            {
                Version = BasaltNative.CREATE_VIRTUAL_DISK_VERSION_1,
                UniqueId = Guid.NewGuid(),
                MaximumSize = 32UL * 1024 * 1024,
                BlockSizeInBytes = 0,
                SectorSizeInBytes = 0
            };
            uint r = BasaltNative.CreateVirtualDisk(
                ref storageType, path,
                BasaltNative.VIRTUAL_DISK_ACCESS_CREATE,
                IntPtr.Zero,
                BasaltNative.CREATE_VIRTUAL_DISK_FLAGS_FULL_PHYSICAL_ALLOCATION,
                0, ref createParams, IntPtr.Zero, out IntPtr h);
            sb.AppendLine($"CreateVirtualDisk      = 0x{r:X8}  ({ErrorName(r)})");
            BasaltNative.CloseHandle(h);
            if (r != 0) return sb.ToString();

            sb.Append(Run(path));
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
        return sb.ToString();
    }

    private static string ErrorName(uint code) => code switch
    {
        0 => "ERROR_SUCCESS",
        2 => "ERROR_FILE_NOT_FOUND",
        3 => "ERROR_PATH_NOT_FOUND",
        5 => "ERROR_ACCESS_DENIED",
        6 => "ERROR_INVALID_HANDLE",
        32 => "ERROR_SHARING_VIOLATION",
        50 => "ERROR_NOT_SUPPORTED",
        87 => "ERROR_INVALID_PARAMETER",
        122 => "ERROR_INSUFFICIENT_BUFFER",
        1359 => "ERROR_INTERNAL_ERROR",
        _ => ""
    };
}