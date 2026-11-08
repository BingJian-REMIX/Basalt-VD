using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Basalt.Native;
using Microsoft.Management.Infrastructure;

namespace Basalt.Services;

/// <summary>负责 VHD/VHDX 的挂载与卸载，挂载后通过对比 DriveInfo 找出新盘符。</summary>
public sealed class MountService : IDisposable
{
    private IntPtr _handle;
    private string? _mountedPath;

    public bool IsMounted => _handle != IntPtr.Zero && _mountedPath != null;
    public string? MountedPath => _mountedPath;
    public bool MountedReadOnly { get; private set; }
    public char? MountedDriveLetter { get; private set; }

    /// <summary>挂载 VHD，返回新分配的盘符；失败抛异常。</summary>
    public char MountVHD(string path, bool readOnly)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("找不到虚拟磁盘文件。", path);

        var before = DriveInfo.GetDrives()
            .Select(d => d.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var storageType = new BasaltNative.VIRTUAL_STORAGE_TYPE
        {
            DeviceId = BasaltNative.GetDeviceId(path),
            VendorId = BasaltNative.VIRTUAL_STORAGE_TYPE_VENDOR_MICROSOFT
        };
        var openParams = new BasaltNative.OPEN_VIRTUAL_DISK_PARAMETERS
        {
            Version = BasaltNative.OPEN_VIRTUAL_DISK_VERSION_1,
            RWDepth = readOnly ? 0u : 1u // OPEN_VIRTUAL_DISK_RW_DEPTH_DEFAULT
        };

        uint result = BasaltNative.OpenVirtualDisk(
            ref storageType, path,
            (readOnly ? BasaltNative.VIRTUAL_DISK_ACCESS_ATTACH_RO : BasaltNative.VIRTUAL_DISK_ACCESS_ATTACH_RW)
                | BasaltNative.VIRTUAL_DISK_ACCESS_GET_INFO,
            BasaltNative.OPEN_VIRTUAL_DISK_FLAG_NONE,
            ref openParams,
            out IntPtr handle);
        if (result != BasaltNative.ERROR_SUCCESS)
            throw new InvalidOperationException($"OpenVirtualDisk 失败，错误码 0x{result:X8}。请以管理员身份运行。");

        try
        {
            var attachParams = new BasaltNative.ATTACH_VIRTUAL_DISK_PARAMETERS { Version = BasaltNative.ATTACH_VIRTUAL_DISK_VERSION_1 };
            uint flags = readOnly ? BasaltNative.ATTACH_VIRTUAL_DISK_FLAG_READ_ONLY : BasaltNative.ATTACH_VIRTUAL_DISK_FLAG_NONE;
            result = BasaltNative.AttachVirtualDisk(
                handle, IntPtr.Zero, flags,
                0, ref attachParams, IntPtr.Zero);

            // 文件带只读属性时 RW 挂载会返回 0x5（拒绝访问），自动降级为只读重试
            if (result == 5 && !readOnly)
            {
                result = BasaltNative.AttachVirtualDisk(
                    handle, IntPtr.Zero, BasaltNative.ATTACH_VIRTUAL_DISK_FLAG_READ_ONLY,
                    0, ref attachParams, IntPtr.Zero);
                if (result == BasaltNative.ERROR_SUCCESS)
                    MountedReadOnly = true;
            }

            if (result != BasaltNative.ERROR_SUCCESS)
                throw new InvalidOperationException(DescribeError(result));

            _handle = handle;
            _mountedPath = path;

            // 主动联机磁盘并分配盘符（确定性）；失败时回退到 DriveInfo 轮询
            char? letter = TryAssignDriveLetter(handle);
            if (letter == null)
                letter = PollForNewDrive(before);
            MountedDriveLetter = letter;
            return letter ?? throw new TimeoutException(
                "已挂载，但未能检测到新盘符。可能原因：① 虚拟磁盘没有分区或未格式化（空磁盘），" +
                "请在“磁盘管理”中初始化磁盘并创建卷；② 系统策略/SAN 策略导致磁盘处于脱机状态；" +
                "③ 本程序未以管理员身份运行。");
        }
        catch
        {
            BasaltNative.CloseHandle(handle);
            _handle = IntPtr.Zero;
            _mountedPath = null;
            throw;
        }
    }

    // ---- 盘符确定：主动联机 + 分配，回退到 DriveInfo 轮询 ----

    /// <summary>
    /// 通过 Windows 存储管理 API 主动把刚挂载的虚拟磁盘联机，并为其首个卷分配盘符。
    /// 返回盘符；无法分配时返回 null（交由回退逻辑处理）。
    /// </summary>
    private static char? TryAssignDriveLetter(IntPtr handle)
    {
        try
        {
            int? diskNumber = GetPhysicalDiskNumber(handle);
            if (diskNumber == null) return null;

            using var session = CimSession.Create(null);
            const string ns = @"root\Microsoft\Windows\Storage";

            var disk = session.QueryInstances(ns, "WQL", $"SELECT * FROM MSFT_Disk WHERE Number={diskNumber}")
                .FirstOrDefault();
            if (disk == null) return null;

            // 磁盘可能因 SAN 策略/签名冲突处于脱机状态，先联机
            try
            {
                session.InvokeMethod(disk, "Online", new CimMethodParametersCollection());
            }
            catch { /* 已联机则忽略 */ }

            var partitions = new List<CimInstance>();
            for (int attempt = 0; attempt < 10 && partitions.Count == 0; attempt++)
            {
                partitions = session.QueryInstances(ns, "WQL", $"SELECT * FROM MSFT_Partition WHERE DiskNumber={diskNumber}")
                    .ToList();
                if (partitions.Count == 0) Thread.Sleep(300);
            }

            char free = FindFreeDriveLetter();
            foreach (var part in partitions)
            {
                string objPath = part.CimSystemProperties.Path?.ToString() ?? "";
                string wql = $"ASSOCIATORS OF {{{objPath}}} WHERE AssocClass=MSFT_PartitionToVolume ResultClass=MSFT_Volume";
                foreach (var vol in session.QueryInstances(ns, "WQL", wql))
                {
                    var existing = vol.CimInstanceProperties["DriveLetter"].Value;
                    if (existing is char ec && ec != '\0')
                        return WaitReady(ec);

                    var p = new CimMethodParametersCollection
                    {
                        CimMethodParameter.Create("DriveLetter", free, CimFlags.In)
                    };
                    var res = session.InvokeMethod(vol, "SetDriveLetter", p);
                    var ret = res == null ? 1u : Convert.ToUInt32(res.ReturnValue);
                    if (ret == 0) return WaitReady(free);
                    return null;
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static char? WaitReady(char letter)
    {
        for (int i = 0; i < 25; i++)
        {
            try { if (new DriveInfo($"{letter}:").IsReady) return letter; } catch { }
            Thread.Sleep(200);
        }
        return letter;
    }

    private static int? GetPhysicalDiskNumber(IntPtr handle)
    {
        uint size = 0;
        uint r1 = BasaltNative.GetVirtualDiskPhysicalPath(handle, ref size, IntPtr.Zero);
        if (size == 0) return null;
        IntPtr buf = Marshal.AllocCoTaskMem((int)size);
        try
        {
            uint r2 = BasaltNative.GetVirtualDiskPhysicalPath(handle, ref size, buf);
            string path = Marshal.PtrToStringUni(buf) ?? "";
            if (r2 != BasaltNative.ERROR_SUCCESS) return null;
            var m = Regex.Match(path, @"PhysicalDrive(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(buf);
        }
    }

    private static char FindFreeDriveLetter()
    {
        var used = DriveInfo.GetDrives()
            .Select(d => char.ToUpperInvariant(d.Name[0]))
            .ToHashSet();
        for (char c = 'Z'; c >= 'D'; c--)
            if (!used.Contains(c)) return c;
        throw new InvalidOperationException("没有可用的盘符。");
    }

    private static char? PollForNewDrive(HashSet<string> before)
    {
        for (int i = 0; i < 50; i++)
        {
            Thread.Sleep(200);
            var letter = DriveInfo.GetDrives()
                .Where(d => !before.Contains(d.Name))
                .Select(d => d.Name.TrimEnd(':', '\\').FirstOrDefault())
                .FirstOrDefault(c => c != '\0');
            if (letter != '\0') return WaitReady(letter);
        }
        return null;
    }

    /// <summary>卸载当前挂载的 VHD。</summary>
    public void UnmountVHD()
    {
        if (_handle == IntPtr.Zero)
            return;

        uint result = BasaltNative.DetachVirtualDisk(_handle, BasaltNative.DETACH_VIRTUAL_DISK_FLAG_NONE, 0);
        BasaltNative.CloseHandle(_handle);
        _handle = IntPtr.Zero;
        _mountedPath = null;
        MountedDriveLetter = null;
        MountedReadOnly = false;

        if (result != BasaltNative.ERROR_SUCCESS)
            throw new InvalidOperationException($"DetachVirtualDisk 失败，错误码 0x{result:X8}。");
    }

    private static string DescribeError(uint code) => code switch
    {
        5 => "AttachVirtualDisk 失败（0x00000005，拒绝访问）。可能原因：1) VHD 文件带只读属性且被其他程序占用；2) 文件位于网络位置或无读取权限；3) 组策略禁止挂载 VHD。",
        2 => "AttachVirtualDisk 失败（找不到文件）。",
        32 => "AttachVirtualDisk 失败（文件被其他程序占用）。",
        _ => $"AttachVirtualDisk 失败，错误码 0x{code:X8}。"
    };

    public void Dispose() => UnmountVHD();
}
