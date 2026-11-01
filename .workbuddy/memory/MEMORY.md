# Basalt — 项目长期记忆

> Basalt（玄武岩）WPF 虚拟磁盘管理器。路径：`D:\data\work\Basalt`（2026-11-01 从 `D:\data\work\quartz\Basalt` 迁出，独立工作区）。
> 目标框架 `net10.0-windows`（本机 SDK 10.0.400），WPF + MVVM。

## 架构

- `Native/BasaltNative.cs` — virtdisk.dll P/Invoke
- `Services/MountService.cs` — VHD 系统挂载（OpenVirtualDisk/AttachVirtualDisk + Windows Storage CIM API 分配盘符）
- `Services/VhdDiskService.cs` — VHD 引擎（包装 MountService，真实文件系统路径）
- `Services/VmdkDiskService.cs` — VMDK 引擎（LTRData.DiscUtils 纯内存解析，伪路径 `"0\DIR\FILE"`，首段为分区索引）
- `Services/DiskEngineFactory.cs` — 按扩展名选引擎
- `Services/UiSelfTest.cs` / `WpfRenderTest.cs` — 无界面自测
- `Common/RangeObservableCollection.cs` — 支持批量 Reset 的 ObservableCollection
- `ViewModels/MainViewModel.cs` + `Models/FileItem.cs`（FolderNode 懒加载目录树）

UI：无边框窗口、左 250px TreeView 懒加载目录树、右侧 GridView（表头点击排序）、右键菜单（刷新/提取到本地）、Light/Dark 双主题（DynamicResource）。

## 关键约束（踩过的坑，别再犯）

1. **必须用 `LTRData.DiscUtils.*` 1.0.47，不能用原版 DiscUtils 0.16.13**。0.16.13 的 `Disk.OpenDisk(path,...)` 对 VMDK 直接返回 null，拆分 VMDK 完全解析不了。
2. **必须显式注册提供器**：`DiscUtils.Setup.SetupHelper.RegisterAssembly(...)` 对 Core/Vmdk/Fat/Ntfs 四个程序集，否则 `DetectFileSystems` 恒空。
3. **FAT 打开必须传非 null 参数**：`info.Open(stream, new FileSystemParameters())`。LTRData 的 `FatFileSystemOptions` 在参数为 null 时 NRE（NTFS 无此问题）。
4. **VHD 的 P/Invoke 常量**：`VIRTUAL_DISK_ACCESS_ATTACH_RO=0x00010000` / `ATTACH_RW=0x00020000`（初版写反）；`ATTACH_VIRTUAL_DISK_FLAG_READ_ONLY=0x1`（0x2 是 NO_DRIVE_LETTER）；Version1 的 `RWDepth` 字段 RW 挂载必须为 1，否则 Open 返回 0x57。
5. **`GetVirtualDiskPhysicalPath` 的 `IntPtr` 签名**：用 `StringBuilder` 传 null 会返回 0x5；改 `IntPtr` + 先传 `IntPtr.Zero` 查长度，且 OpenVirtualDisk 的 access mask 必须含 `VIRTUAL_DISK_ACCESS_GET_INFO`。
6. **集合必须是 `RangeObservableCollection`**：`List<T>` 不触发 `CollectionChanged`，TreeView 会退化成"调整窗口大小才显示"。
7. **报告文件用 UTF-8 写**（`WriteReport`）：默认编码导致中文乱码。无界面模式禁止弹 MessageBox（`App.Headless`）。
8. **VHD 挂载需管理员权限**（app.manifest 已 requireAdministrator）。

## 自测命令（都写 UTF-8 报告到 %TEMP%）

| 命令 | 用途 |
|---|---|
| `Basalt.exe --uitest <路径>` | 驱动 ViewModel，统计集合通知次数 + 模拟点击/双击 |
| `Basalt.exe --rendertest <路径>` | 真实 Show() 窗口遍历视觉树，统计 TreeViewItem/ListViewItem 容器数（证明不 resize 也已渲染） |
| `Basalt.exe --mounttest <路径>` | VHD 挂载测试 |
| `Basalt.exe --diag <路径>` / `--selftest` | virtdisk P/Invoke 逐步诊断 |

> 渲染自测必须 `Show()` + `UpdateLayout()`，只 Measure/Arrange 不足以触发 TreeViewItem 容器生成。

## 已知限制

- **ext4/Linux 文件系统读不了**：DiscUtils/LTRData 不支持 ext 家族。Ubuntu.vmdk 的分区能列出（标注"暂不支持读取"），但文件读不出。可行路线（未做）：`qemu-img` 转 VHDX 后 `wsl --mount --vhd ... -p 1`；或 guestfish/libguestfs。
- **VMDK 被 VMware / Defender 扫描独占**时报 "file used by another process"，需确保 VM 关机、无锁。

## 测试素材

- `E:\VSstopmashanhi\win11\Windows 11 x64.vmdk`（twoGbMaxExtentSparse，26 分片）→ 5 分区，4 可读（FAT32 EFI + 3×NTFS）
- `E:\VSstopmashanhi\Ubuntu\Ubuntu.vmdk`（6 分片）→ 2 分区，均 ext4/BIOS Boot
