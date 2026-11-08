# Basalt

Windows 虚拟磁盘 / 归档镜像管理器。把 **VHD / VHDX、VMDK、压缩包、ISO、自解压程序** 统一成一个可浏览的文件树，文件内容在应用内以**纯内存流预览**，不把文件偷懒丢给外部程序。

- 仓库：<https://cnb.cool/rlrs-studio/basalt-vd>
- 技术栈：.NET 10 · WPF · C# · MVVM · Windows 专用
- 依赖：LTRData.DiscUtils 1.0.47、Microsoft.Management.Infrastructure 3.0.0、系统 7-Zip CLI

---

## 1. 特性速览

| 能力 | 说明 |
|---|---|
| 三引擎统一 | VHD/VHDX 系统挂载（virtdisk P/Invoke + CIM 盘符）、VMDK 纯内存解析（DiscUtils，多分片 sparse）、容器归档（7-Zip CLI 列举与提取） |
| 容器浏览 | 按魔数识别 ZIP / 7z / RAR / CAB / ISO9660·UDF / GZIP / TAR / XZ / BZIP2 / Zstd / SFX；支持镜像内嵌套展开与逐级返回 |
| 内部预览 | 文本 / 图片 / 十六进制三个独立窗口，全部走 `IVirtualDisk.OpenRead` 流，不落地、不依赖文件路径 |
| 文件管理 | 左侧懒加载目录树、右侧 DataGrid 原生 HICON、可点击面包屑、`..` 返回上级、表头排序、属性窗口、状态栏统计 |
| 磁盘工具 | 深度清理、新建空白 VHDX（MBR/GPT + NTFS）、归档转 VHDX、文件夹递归提取（进度条 / 取消） |
| 生态联动 | 与 Chert 启动器联动（GUI 优先）、CLI 静默挂载 daemon，供后台调用 |
| 体验 | 无边框窗口、亮/暗双主题、7z 中文防乱码、临时文件退出自动清理 |

---

## 2. 环境要求

| 项 | 要求 |
|---|---|
| 操作系统 | Windows（WPF + virtdisk API，不支持跨平台） |
| SDK | .NET 10 SDK（`net10.0-windows`，`UseWPF=true`） |
| 权限 | **管理员**。`app.manifest` 声明 `requireAdministrator`，VHD 挂载与盘符分配依赖它 |
| 7-Zip | 需安装 7-Zip。依次探测 `D:\Program Files\7-Zip\7z.exe`、`C:\Program Files\7-Zip\7z.exe`、`C:\Program Files (x86)\...`、`D:\Program Files (x86)\...`，最后回退 `PATH` |
| NuGet | 首次构建需能还原 DiscUtils 相关包（离线可复用本机 `%USERPROFILE%\.nuget\packages` 缓存） |

---

## 3. 快速开始

```bash
git clone https://cnb.cool/rlrs-studio/basalt-vd.git
cd basalt-vd/src

dotnet restore
dotnet build -c Release
./bin/Release/net10.0-windows/Basalt.exe        # 直接双击运行亦可（会请求提权）
```

输出目录为 `src/bin/<Configuration>/net10.0-windows/`，已被 `.gitignore` 忽略。仓库根目录只放元文件，**项目根在 `src/`**，所有构建命令都在 `src/` 下执行。

主界面「选择并挂载 VHD / VMDK」默认过滤 `*.vhd;*.vhdx;*.vmdk`。归档 / ISO / SFX 可在文件名框直接输入完整路径（引擎按魔数识别），或在已打开的镜像内双击归档「展开为容器」逐层浏览。

---

## 4. 三种磁盘引擎

`Services/DiskEngineFactory.cs` 按文件类型分流，三种实现共享 `IVirtualDisk` 接口：

| 引擎 | 实现 | 路径语义 | `OpenRead` 来源 | 可写 |
|---|---|---|---|---|
| VHD / VHDX | `VhdDiskService` | 真实文件系统路径（如 `Z:\DIR`） | `FileStream` | 是（非只读挂载时） |
| VMDK | `VmdkDiskService` | 伪路径：`0` = 第 0 个分区根，`0\EFI\Boot` = 分区内路径 | DiscUtils 文件系统流 | 否（只读解析，不改动原始镜像） |
| 容器 | `ContainerDiskService` | 归档内部路径（`src\a.txt`） | `7z e -so` 的 stdout → `MemoryStream` | 否（只读列举） |

`IVirtualDisk` 统一提供：列目录 / 列文件 / 提取 / 父路径 / 元数据 / 原生图标 / 面包屑 / `OpenRead` / 实体化到临时文件 / 系统默认程序打开。新增引擎只需实现该接口。

> SFX 自解压 exe **绝不会被执行**：仅让 7-Zip 以归档方式尝试列举，成功才视为容器。

---

## 5. 界面与交互

### 工具栏

`选择并挂载 VHD / VMDK` · `卸载` · `↑ 上级` · `刷新` · `生成虚拟磁盘`（选中 `.zip`/`.7z` 时转 VHDX） · `新建虚拟磁盘` · `在 Chert 中启动` · 标题栏最右侧为亮/暗主题切换。

### 右键菜单

`打开` · `预览` · `展开为容器` · `提取到本地` · `深度清理` · `导出到 Chert 工作目录` · `生成虚拟磁盘 (.vhdx)` · `属性` · `刷新目录` · `返回上级`。

双击：目录 → 进入；容器 → 展开；普通文件 → 走预览分发器。列表顶部有 `..` 虚拟项返回上级。

### 内部预览分发（`Services/PreviewDispatcher.cs`）

| 类型 | 扩展名 | 行为 |
|---|---|---|
| 文本 | `.txt .md .json .cs .xml .log .xaml .py …` | `TextViewerWindow`：上限 2 MB / 5000 行，等宽字体，BOM 识别 + UTF-8 → GB18030 兜底 |
| 图片 | `.png .jpg .bmp .ico .gif .tif …` | `ImageViewerWindow`：`Stream → BitmapImage`（`OnLoad` + `Freeze`），滚轮缩放 |
| 十六进制 | `.dll .exe .bin .so` 及**所有未知格式** | `HexViewerWindow`：前 4 KB，16 字节/行（偏移 + HEX + ASCII） |
| 外部兜底 | `.pdf .docx .xlsx .pptx .mp4 …` 白名单 | 调用系统默认程序；VMDK / 容器先落地到临时文件 |

**二进制嗅探**：文本分支先读前 100 字节，出现 `\0` 或控制字符（排除 TAB/LF/CR，含 `0x7F`）占比 ≥ 10% 即判为二进制，自动降级到 `HexViewerWindow`，避免满屏 `  ` 乱码。UTF-16 LE/BE BOM 是白名单例外（天然含 `\0`）。UTF-8 / GBK 高位字节不计入控制字符，中文不会被误判。

> 边界：含大量 ANSI 颜色转义（`ESC 0x1B`）的 `.log` 可能因控制字符超阈值被判二进制，阈值常量为 `BinaryControlPercent`。

所有外部打开产生的临时文件由 `Common/TempFileRegistry.cs` 在 `App.OnExit` 统一清理。

### 深度清理（`Services/CleanupService.cs`）

递归统计挂载盘 / 分区根下的三个目标：`$Recycle.Bin`、`Windows\Temp`、`Windows\SoftwareDistribution\Download`，窗口显示「发现 X 可回收内容」+ 每项大小/文件数，勾选后一键清理。

安全措施：删前二次确认、目标必须位于盘符根之下、跳过 reparse point、逐文件捕获 `UnauthorizedAccessException` / `IOException`、只删内容保留目录、汇总跳过原因。

**扫描三引擎通用，一键删除仅在 VHD 可写挂载时启用** —— VMDK 以 `FileAccess.Read` 打开、容器是只读归档，写回会破坏原始镜像，因此只读引擎只展示统计并说明原因。

### 新建虚拟磁盘 / 归档转 VHDX

- `NewDiskWindow`：输入磁盘名称、保存路径、容量（1–2048 GB）、分区格式（MBR / GPT）。`EmptyVhdxService` 用纯托管链路生成：动态 VHDX → `BiosPartitionTable` 或 `GuidPartitionTable` → 单个 NTFS 数据分区 → `NtfsFileSystem.Format`。不需要管理员权限、不调用系统 `format`。
- `ArchiveToVhdxService`：选中 `.zip` / `.7z` → 7z 解压到临时目录 → 同样的 VHDX 链路 → 逐文件写入，产出一个可直接挂载的 `.vhdx`。

> VHDX 是块设备镜像，必须有分区表 + 文件系统，不能把文件流直接「封装」进去。

### 文件夹递归提取（`Services/RecursiveExtractor.cs`）

右键文件夹 → 递归 `GetDirectories` / `GetFiles` 收集相对路径 → 本地 `Directory.CreateDirectory` 重建目录树（空目录也还原） → 逐个 `IVirtualDisk.OpenRead` 流 `CopyTo` 写入。含 zip-slip 越界防护、逐层异常捕获、取消令牌；`ProgressWindow` 提供扫描 Loading → 确定进度条 → 完成汇总。

---

## 6. Chert 联动（`Services/ChertIntegration.cs`）

| 功能 | 行为 |
|---|---|
| 工作目录 | 读 `%APPDATA%\Chert\gameroot.txt`，不存在/损坏时回落 `%APPDATA%\.minecraft`（与 Chert.Core `GameConstants` 口径一致） |
| 在 Chert 中启动 | **GUI 优先**：拉起 `Chert Launcher.exe` / `Chert.App.exe` 并附 `--game-dir <路径> --type=minecraft`；仅当检测到 `chert://` 已注册时才走 URL Scheme（留给下一版 Chert） |
| 导出到工作目录 | 右键把选中文件复制进 Chert 的 `.minecraft` 目录 |
| 路径覆盖 | 环境变量 `BASALT_CHERT_GUI` 可指定 GUI 可执行文件 |

当前 Chert GUI 不解析命令行参数，`--game-dir` 是预留参数，不影响启动。`chert://` 在本机 HKCR/HKCU 均未注册。

---

## 7. CLI 自动化接口（`Services/CliMount.cs`）

供 Chert 等外部程序在后台调用：

```bash
# 挂载（默认只做预检：挂载成功后立即卸载退出，不阻塞调用方）
basalt.exe mount "C:\test.vhdx" --readonly --silent

# 常驻挂载：进程保持存活，盘符持续存在
basalt.exe mount "C:\test.vhdx" --readonly --silent --daemon [--port N]

# 停止常驻进程（loopback TCP 信号）
basalt.exe stop [--port N] [--token T]
```

| 参数 / 产物 | 说明 |
|---|---|
| `--readonly` / `--silent` | 只读挂载 / 静默（不弹任何窗口） |
| `--daemon` | 主线程阻塞保活；stdin 输入 `stop` 或 loopback TCP 发 `STOP <token>` 才 `DetachVirtualDisk` 退出 |
| `--port N` | 停止信号监听端口，默认 `47821`；被占用时自动顺延最多 10 个端口，`0` 表示由系统分配 |
| 状态文件 | `%TEMP%\basalt_mount_state.json`（pid / port / token / 盘符 / 路径 / 只读标记），`stop` 自动读取 |
| 日志 | `%TEMP%\basalt_cli.log`，同时尝试输出到父控制台 |
| 退出码 | `0` 成功，`1` 挂载/停止失败，`2` 参数或文件错误 |

---

## 8. 自测与回归

所有自测都是**无界面模式**（`App.Headless`）：不弹 MessageBox，报告以 UTF-8 写入 `%TEMP%\basalt_*.txt` 后自动退出。

```bash
cd src
./bin/Release/net10.0-windows/Basalt.exe --previewtest
```

报告在 `%TEMP%` 下，用 PowerShell 读取（UTF-8）：

```powershell
Get-Content "$env:TEMP\basalt_previewtest.txt" -Encoding UTF8
```

| 命令 | 覆盖范围 | 报告 |
|---|---|---|
| `--selftest` | virtdisk P/Invoke 常量自检 | `basalt_selftest.txt` |
| `--diag <vhd>` | 单盘逐步挂载诊断 | `basalt_diag.txt` |
| `--mounttest <vhd>` | 挂载链路 | `basalt_mounttest.txt` |
| `--uitest <path>` | 树 / 列表集合通知与交互数据链 | `basalt_uitest.txt` |
| `--rendertest <path> [--light]` | 主窗口视觉树渲染（暗/亮） | `basalt_rendertest*.txt` |
| `--featuretest <path>` | `..` / 面包屑 / 元数据 / 图标 / 状态栏 / 提取 | `basalt_featuretest.txt` |
| `--containertest <path>` | 归档识别、列举、单文件提取 | `basalt_containertest.txt` |
| `--containerui <path>` | 容器内浏览窗口渲染 + 截图 | `basalt_containerui.txt` |
| `--nestedtest <path>` | VMDK/VHD 内嵌归档全链路 | `basalt_nestedtest.txt` |
| `--previewtest [path]` | 预览分发 / 二进制嗅探 / 7z 编码 / 归档转 VHDX | `basalt_previewtest.txt` |
| `--vhdxtest <path>` | VHDX 回读校验 | `basalt_vhdxtest.txt` |
| `--clitest` | CLI daemon 端口、token、停止信号 | `basalt_clitest.txt` |
| `--deeptest` | 递归提取 / 深度清理 / MBR+GPT 空白 VHDX | `basalt_deeptest.txt` |
| `--deepui [--light]` | 新增窗口渲染（暗/亮） | `basalt_deepui*.txt` |
| `--previewui [--light]` | 三个预览窗口渲染（暗/亮） | `basalt_previewui*.txt` |

渲染类自测会同时把截图写到 `%TEMP%\basalt_*.png`。当前 Release 基线：**15 组回归 0 失败套件**，Debug / Release 均 0 警告 0 错误。

---

## 9. 项目结构

仓库根只保留 `.gitignore` / `.gitattributes` / `.workbuddy`（项目长期技术笔记，有意纳入版本管理），源码全部在 `src/`：

```
src/
├── Basalt.csproj          项目文件（net10.0-windows / WPF / NuGet 依赖）
├── App.xaml(.cs)          启动、主题、CLI 与自测入口、退出清理
├── app.manifest           requireAdministrator
├── Assets/                图标（含多尺寸 ICO）与主题预览图
├── Themes/                Dark.xaml / Light.xaml（DynamicResource 配色）
├── Views/                 8 个窗口 × XAML + code-behind
├── ViewModels/            MainViewModel / BreadcrumbItem / ViewModelBase
├── Models/                FileItem.cs（FileItem / FileMetadata / FolderNode / FileItemFormatter）
├── Services/              14 个业务服务（引擎、预览、清理、VHDX、CLI、Chert…）
├── Common/                IconProvider / TempFileRegistry / RangeObservableCollection
├── Native/                virtdisk P/Invoke 与诊断
├── Properties/            AssemblyInfo.cs
└── Tests/                 12 个自测 / 渲染测试
```

关键文件：`Services/IVirtualDisk.cs`（引擎抽象）、`Services/DiskEngineFactory.cs`（分流）、`Services/PreviewDispatcher.cs`（预览分发与嗅探）、`ViewModels/MainViewModel.cs`（UI 状态与命令）、`Services/CliMount.cs`（CLI）。

`.gitattributes` 统一 `eol=lf`，图片/可执行文件按二进制处理。

---

## 10. 架构要点

- **统一流抽象**：`IVirtualDisk.OpenRead` 屏蔽三种引擎的物理差异，所有内部预览只认 `Stream`。容器引擎读取时不会重新 `OpenRead`（嗅探消耗的头部通过 `prefix` 拼回），避免重复启动 7z 进程。
- **编码**：启动时注册 `CodePagesEncodingProvider`；7z 列目录加 `-sccUTF-8`，原始字节按 UTF-8 / GB18030 双解码，中文目录名不乱码。
- **图标**：VHD 用 `ExtractAssociatedIcon` 取真实图标，VMDK / 容器按扩展名取关联图标，句柄统一 `DestroyIcon` 释放。
- **主题**：`DynamicResource` 绑定 `Themes/*.xaml`，预览窗口跟随主窗口亮暗；选中行暗色 `#334F8CFF`、亮色 `#E5E7EB`。
- **临时文件**：VMDK / 容器交给外部程序前落地到 `%TEMP%\BasaltOpen`，退出时统一清理。

---

## 11. 已知限制

| 项 | 说明 |
|---|---|
| ext4 / Linux 分区 | DiscUtils 不支持，只能列出分区并标注不可读 |
| 权限 | VHD 挂载需要管理员。非提权环境下 `AttachVirtualDisk` 可能成功但 CIM 盘符分配失败，CLI daemon 全流程无法实机验证 |
| `chert://` | 当前版本 Chert 未注册该 URL Scheme，按钮走 GUI 回退 |
| 深度清理 | 只读引擎（VMDK / 容器 / 只读 VHD）只扫描统计，不提供删除 |
| 归档转 VHDX | 仅 `.zip` / `.7z` 启用；NTFS 元数据会预分配，实际占用远大于归档体积 |
| 遗留 | `src/Tests/` 下文件 namespace 仍为 `Basalt.Services`（重组目录时未改，避免牵动 `App.xaml.cs` 引用） |

---

## 12. 开发约定

- 提交前至少跑 `dotnet build -c Release` 与 `--previewtest` / `--deeptest`，UI 改动另跑 `--deepui` / `--previewui` 暗亮两轮。
- 推送到 CNB 需要带 `repo-code:rw` 权限的令牌；凭据只通过一次性 HTTP header 传入，**不要写进 `.git/config` 或提交到仓库**。
- 新增磁盘引擎请实现 `IVirtualDisk` 并在 `DiskEngineFactory` 注册；新增自测请同时更新本文件的命令表。
