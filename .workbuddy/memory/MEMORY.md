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
- `D:\data\inside nova\WIN光盘\wepe64\WePE64_V2.2.iso`（容器回归用）

## 2026-11-08 增量（详见当日日志）

- `IVirtualDisk.OpenRead` 统一三引擎流：VHD=FileStream / VMDK=DiscUtils 流 / 容器=`7z e -so`→MemoryStream。内部预览**绝不落地、不偷懒 Process.Start**。
- `PreviewDispatcher` + `TextViewerWindow` / `ImageViewerWindow` / `HexViewerWindow`；仅 `.pdf/.docx/...` 白名单走外部；`TempFileRegistry` 在 `App.OnExit` 清理临时文件（**不要登记 VHD 真实路径**）。
- `ArchiveToVhdxService`：7z x → `DiscUtils.Vhdx.Disk.InitializeDynamic` → MBR → `NtfsFileSystem.Format`。新增包 `LTRData.DiscUtils.Vhdx 1.0.47`。VHDX 是块设备镜像，不能"流式封装"文件。
- `ChertIntegration`：Chert GUI 优先（源码 `D:\data\work\Claw\MCLCS-WPF-clean`）；工作目录读 `%APPDATA%\Chert\gameroot.txt`，回落 `%APPDATA%\.minecraft`；`chert://` 仅在 Scheme 已注册时用（下一版 Chert）。
- `CliMount`：`basalt.exe mount <vhd> --readonly --silent --daemon [--port N]` 常驻；`basalt.exe stop` 发 loopback TCP 停止信号；状态文件 `%TEMP%\basalt_mount_state.json`。
- 7z 列目录加 `-sccUTF-8` + 原始字节 UTF-8/GB18030 双解码；`App.OnStartup` 注册 `CodePagesEncodingProvider`。
- 新自测：`--previewtest [路径]` / `--vhdxtest <归档>` / `--clitest` / `--previewui [--light]`。
- **本机非管理员**：VHD 盘符分配失败（`Mount-DiskImage` 报无特权），daemon 全流程未实机验证；TCP 停止通道已由 `--clitest` 覆盖。
