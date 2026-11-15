# Basalt

Windows 虚拟磁盘 / 归档镜像管理器。把 **VHD / VHDX、VMDK、压缩包、ISO、自解压程序** 统一成一个可浏览的文件树，文件内容在应用内以**纯内存流预览**，不把文件偷懒丢给外部程序。

- 主仓库：<https://cnb.cool/rlrs-studio/basalt-vd> · GitHub 镜像：<https://github.com/BingJian-REMIX/Basalt-VD>
- 技术栈：.NET 10 · WPF · C# · MVVM · Windows 专用
- 依赖：LTRData.DiscUtils 1.0.47（Core / Fat / Ntfs / Vmdk / Vhdx / **Udf / Iso9660**）、Microsoft.Management.Infrastructure 3.0.0、系统 7-Zip CLI、随程序分发的 **wimlib-imagex 1.14.5**（`tools/wimlib-x64/`，官方 Windows x86_64 二进制，GPLv3+ / libwim LGPL，许可证与来源同目录）

---

## 1. 特性速览

| 能力 | 说明 |
|---|---|
| 三引擎统一 | VHD/VHDX 系统挂载（virtdisk P/Invoke + CIM 盘符）、VMDK 纯内存解析（DiscUtils，多分片 sparse）、容器归档（7-Zip CLI 列举与提取） |
| 容器浏览 | 按魔数识别 ZIP / 7z / RAR / CAB / ISO9660·UDF / GZIP / TAR / XZ / BZIP2 / Zstd / SFX；支持镜像内嵌套展开与逐级返回 |
| 内部预览 | 文本 / 图片 / 十六进制三个独立窗口，全部走 `IVirtualDisk.OpenRead` 流，不落地、不依赖文件路径 |
| 文件管理 | 左侧懒加载目录树、右侧 DataGrid 原生 HICON、可点击面包屑、`..` 返回上级、表头排序、属性窗口、状态栏统计 |
| 磁盘工具 | 深度清理、新建空白 VHDX（MBR/GPT + NTFS）、归档转 VHDX、文件夹递归提取（进度条 / 取消） |
| ISO 检查器 | 打开 ISO 自动定位 `sources`、读取 `install.wim`/`install.esd` 大小、纯托管解析 WIM XML 版本列表（支持 4~6 GB 映像的尾部元数据随机读取）、提取安装映像 |
| 一键精简 ISO | 选中版本 + 勾选精简项 → wimlib apply / 清理 / capture → 原位替换 ISO 副本中的 `install.wim` → 输出全新精简版 ISO；四阶段进度条 + 取消 + 完成后自动定位输出目录 |
| 生态联动 | 与 Chert 启动器联动（GUI 优先）、CLI 静默挂载 daemon，供后台调用 |
| 体验 | 无边框窗口、亮/暗双主题、7z 中文防乱码、临时文件退出自动清理 |

---

## 2. 环境要求

| 项 | 要求 |
|---|---|
| 操作系统 | Windows（WPF + virtdisk API，不支持跨平台） |
| SDK | .NET 10 SDK（`net10.0-windows`，`UseWPF=true`） |
| 权限 | **管理员**。`app.manifest` 声明 `requireAdministrator`，VHD 挂载与盘符分配依赖它；精简结束后夺权清理 TrustedInstaller ACL 的临时目录同样依赖它 |
| 7-Zip | 需安装 7-Zip。依次探测 `D:\Program Files\7-Zip\7z.exe`、`C:\Program Files\7-Zip\7z.exe`、`C:\Program Files (x86)\...`、`D:\Program Files (x86)\...`，最后回退 `PATH` |
| NuGet | 首次构建需能还原 DiscUtils 相关包（离线可复用本机 `%USERPROFILE%\.nuget\packages` 缓存） |
| WIM 引擎 | 优先用随程序分发的 `tools\wimlib-x64\wimlib-imagex.exe`（构建时自动复制到输出目录，无需安装、无需提权）；缺失时回退到系统 `dism.exe`（需提权）。可用环境变量 `BASALT_WIMLIB` 指定其它副本。wimlib `apply` 会还原映像里的只读属性与安全描述符，因此清理链路显式处理这两者 |
| 精简磁盘空间 | 完整精简需要 `源 WIM + 展开目录树 + 新 WIM + 输出 ISO` 的空间。5.34 GiB 的 Win11 ISO 实测预估约 **27 GB**；工作目录默认 `%TEMP%\Basalt_Wim_Work`，空间不足时自动挑剩余空间最大的固定盘，并可在检查器里手动指定 |

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

主界面「选择并挂载 VHD / VMDK」过滤 `*.vhd;*.vhdx;*.vmdk;*.iso`。其它归档 / SFX 可在文件名框直接输入完整路径（引擎按魔数识别），或在已打开的镜像内双击归档「展开为容器」逐层浏览。打开 ISO 时会自动弹出 `IsoInspectorWindow`。

---

## 4. 三种磁盘引擎

`Services/DiskEngineFactory.cs` 按文件类型分流，三种实现共享 `IVirtualDisk` 接口：

| 引擎 | 实现 | 路径语义 | `OpenRead` 来源 | 可写 |
|---|---|---|---|---|
| VHD / VHDX | `VhdDiskService` | 真实文件系统路径（如 `Z:\DIR`） | `FileStream` | 是（非只读挂载时） |
| VMDK | `VmdkDiskService` | 伪路径：`0` = 第 0 个分区根，`0\EFI\Boot` = 分区内路径 | DiscUtils 文件系统流 | 否（只读解析，不改动原始镜像） |
| 容器 | `ContainerDiskService` | 归档内部路径（`src\a.txt`） | `7z e -so` 的 stdout → `MemoryStream` | 否（只读列举） |

`IVirtualDisk` 统一提供：列目录 / 列文件 / 提取 / 父路径 / 元数据 / 原生图标 / 面包屑 / `OpenRead` / `ReadHead`（默认实现，容器引擎重写为 7z 早停）/ 实体化到临时文件 / 系统默认程序打开。新增引擎只需实现该接口。

> SFX 自解压 exe **绝不会被执行**：仅让 7-Zip 以归档方式尝试列举，成功才视为容器。

---

## 5. 界面与交互

### 工具栏

`选择并挂载 VHD / VMDK` · `卸载` · `↑ 上级` · `刷新` · `生成虚拟磁盘`（选中 `.zip`/`.7z` 时转 VHDX） · `新建虚拟磁盘` · `ISO 检查器`（当前为 ISO 时启用） · `在 Chert 中启动` · 标题栏最右侧为亮/暗主题切换。

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

### ISO 检查器（`Views/IsoInspectorWindow.xaml`）

打开 `.iso`（直接挂载或「展开为容器」）时自动非模态弹出；主窗口文件列表同步跳到 `sources`。工具栏 `ISO 检查器` 可随时重开。

| 区块 | 行为 |
|---|---|
| 概览 | `sources` 路径（大小写不敏感）、`install.wim` / `install.esd` 名称与大小、WIM 元数据摘要 |
| 版本列表 | `ListBox` 单选，可点选要精简的版本；`WimMetadataReader` 读 208 字节 WIM 头 → `rhXmlData`（+0x48）拿到 XML 绝对偏移 → 按偏移随机读回未压缩 UTF-16LE XML → 解析 `<IMAGE>` 的 `DISPLAYNAME` / `VERSION` / `ARCH` / `EDITIONID` / `TOTALBYTES` |
| 文件清单 | `sources` 下的文件与大小，安装映像高亮；区块独立限高滚动 |
| 精简选项 | `移除 Windows Defender` / `移除 Edge、Xbox` / `移除冗余旧版驱动` + `保留其它版本` + `工作目录`；「开始精简」在选中版本、勾选至少一项、引擎可用且源为本地 ISO 时激活。该区块固定在窗口底部 |
| 提取 | `提取 install.wim 到本地`：SaveFileDialog → 后台线程 `IVirtualDisk.ExtractFile` |

**两阶段读取（关键）**：官方 Windows ISO 的 `install.wim` 有 4~6 GB，且 XML 元数据写在**文件尾部**（实测 Win11 22H2 在 4.59 GB 处，16 034 字节）。`OpenRead` 会把整个文件灌进 `MemoryStream`，而容器的 `7z e -so` 只能从头顺序输出，跳到尾部要让 7-Zip 白吐约 5 GB（实测 1 GB ≈ 9 秒）。因此：

1. `IVirtualDisk.ReadHead(path, 208)` 只取 WIM 头，解析出 XML 的绝对偏移与大小；
2. `IVirtualDisk.ReadRange(path, offset, size)` 精确取回那 16 KB。`ContainerDiskService` 对 ISO 走 `IsoRandomAccess`（DiscUtils `UdfReader` / `CDReader` 的可寻址流，UDF 优先），毫秒级完成；非光盘容器回退到 `7z -so` 顺序跳过，并对超过 256 MB 的偏移明确降级。

**降级**：XML 资源带 `COMPRESSED(0x04)` / `SOLID(0x10)`（多见于 `install.esd` 的 LZMS）、非 WIM 输入、容器不支持随机访问且偏移过大 —— 均不强行解析，只显示文件清单并允许提取，界面给出明确原因。

**实机验证**（`D:\data\inside nova\WIN光盘\win 11\zh-cn_windows_11_consumer_editions_version_22h2_updated_jan_2023_x64_dvd_0b2e8f08.iso`，5.34 GiB）：7-Zip 列目录 300 ms、`Inspect` 10 ms、全流程 515 ms；`install.wim` 4 931 618 957 字节；解析出 6 个版本（家庭版 / 家庭单语言版 / 教育版 / 专业版 / 专业教育版 / 专业工作站版，均为 `10.0.22621 · x64`，LZX），`sources` 文件清单 186 项。窗口暗/亮主题渲染均已截图核对。

### 一键精简系统镜像（`Services/TrimWorkflow.cs`）

**绝不修改源 ISO 与源 `install.wim`**：所有手术都在工作目录的副本上进行，最后写出一个全新的 `*-basalt-trimmed.iso`（自测可用 `--out` 指定其它磁盘）。

| 阶段 | 实现 |
|---|---|
| 准备 | 校验输入 / 引擎 / 版本；`EstimateRequiredBytes` 预估空间（用 XML 的 `TOTALBYTES` 作展开体积）；`ResolveWorkRoot` 自动挑剩余空间最大的固定盘；输出路径自动避让已有文件 |
| 提取中 | `IsoExtentService.Find` 扫描扇区对齐的 `MSWIM` 魔数，用 WIM 头给出的 XML 偏移排除 `boot.wim`，再用 UDF/ISO9660 文件流抽查若干偏移确认 extent **连续**；随后 `ExtractRaw` 按物理偏移把原 WIM 拷到工作目录（比 7-Zip 解包快得多，实测 4.59 GB / 15 s） |
| 释放中 | `WimTool` 调 `wimlib-imagex apply <wim> <index> <dir>`（或 DISM `/Apply-Image`）；实时解析 `\r` 进度行 |
| 清理中 | `ImageTrimService` 收集目标目录 → **复用** `CleanupService.DeleteTargets(..., removeTargetRoot: true, clearReadOnly: true)`（逐项捕获权限异常、跳过 reparse point、防越界）；System32 下散落的 `SecurityHealth*.dll/exe` 先清 `ReadOnly` 再删。wimlib apply 会同时还原只读属性与 `TrustedInstaller` ACL，两者都挡删除，实测首轮跳过 2 943 个系统文件；因此对删除失败的目标再用 `WindowsAclTool` 夺权重删，并回滚首轮的跳过计数 |
| 封装中 | `wimlib-imagex capture` 生成新 WIM；`WimTool.SetImageProperties` 用 `info --image-property` 把 `NAME` / `DISPLAYNAME` / `DISPLAYDESCRIPTION` 回写成用户选中的版本名（放在 `export` 之后，避免追加映像时重建 XML 覆盖）；勾选「保留其它版本」时用 `export` 把未精简的映像逐个追加（精简后的版本排到索引 #1） |
| 替换中 | 复制源 ISO → 把新 WIM 写回同一 UDF extent → 缩短的部分写成 **NTFS 稀疏空洞** → 其余扇区原样复制；写流释放后回读 WIM 头 + XML 复核 |
| 完成 | 提示「精简版 ISO 已生成」并用资源管理器 `/select` 自动定位输出目录 |

**清理规则已用真实 Win11 22H2 映像的 `wimlib dir` 元数据核对**（132 400 条路径）：

- Defender：`Windows\System32\SecurityHealth`、9 个 `SecurityHealth*` 文件、`Program Files\Windows Defender`、`ProgramData\Microsoft\Windows Defender`、`WinSxS\amd64_windows-defender_*`、`SecHealthUI` UWP 目录。
- Edge / UWP：`Edge` / `EdgeCore` / `EdgeUpdate` / `EdgeWebView` 四个目录（真实映像实测），`SystemApps` 与 `WindowsApps` 下按前缀匹配 Edge / Xbox / Gaming / Zune / Solitaire / FeedbackHub / YourPhone / Skype / BingWeather / GetHelp / People / Cortana / MixedReality。
- 驱动（可选）：同 INF + 体系结构的多版本只保留时间戳最新的一份；另外清理 `prn*` / `mdm*` 遗留打印机与调制解调器驱动包（真实映像有 168 个）。**不会整目录清空 DriverStore**。

**为什么用原位替换而不是重建 ISO**：Windows 安装盘是 UDF + El Torito 引导结构，`install.wim` 常 > 4 GB。7-Zip 不能写 ISO/UDF，DiscUtils `CDBuilder` 只能写 ISO9660（32 位长度），重建会丢引导或截断大文件。原位替换保持 UDF 目录项、文件长度与引导目录完全不变，因此 100% 保留可引导性；WIM 解析器只读头 + 资源表 + XML，尾部多余的零字节会被忽略。逻辑尺寸保持不变是刻意的（改长度就要重写 UDF File Entry 与分区描述符），但 NTFS 稀疏写入让**实际占用随精简量真实下降**。新 WIM 反而比原 extent 大时会明确报错，绝不产出损坏的 ISO。

**取消**：`ProgressWindow` 的取消按钮 → `CancellationTokenSource` → `WimTool` Kill 整棵进程树 → 删除半成品输出与工作目录。阶段文案为「准备中 → 提取中 → 释放中 → 清理中 → 封装中 → 替换中 → 已完成」，确定进度与 Loading 动画按阶段切换。

**工作目录回收（实跑踩过的坑）**：wimlib apply 出来的系统文件同时带 `ReadOnly` 属性与映像里原始的 `TrustedInstaller` owner / ACL，普通乃至管理员令牌都只有 `ReadAndExecute`，`File.Delete` 会抛 `UnauthorizedAccessException`。首轮真实实跑因此在 `E:\Basalt_Wim_Work` 留下 6.6 GB / 87 757 个文件的残骸，下一次精简还会因空间预检失败。`TryDeleteDirectory` 现在分三步：清 `ReadOnly` 后常规删除 → 仍残留时由提权进程通过 `WindowsAclTool` 静默执行 `takeown /R /D Y` + `icacls /grant *S-1-5-32-544:(F) /T` 夺权 → 再删一轮。只对临时副本动 ACL，`capture` 已完成，**输出 WIM 的安全描述符保持原样**。历史残留可用 `--trimcleanup <dir>` 单独清理。

**四轮真实实跑暴露的质量问题（均已修复并有回归断言）**：

| 现象 | 根因 | 修复 |
|---|---|---|
| 2 943 个 Defender 文件被跳过 | apply 同时还原了 `ReadOnly` 属性与 `TrustedInstaller` owner / ACL；只清属性时 `SetAttributes` / `File.Delete` 仍被拒 | `CleanupService` 新增 `clearReadOnly`；`ImageTrimService` 对仍存在的目标用 `WindowsAclTool`（`takeown` + `icacls`）夺权后重删，散落文件失败时同样重试一次 |
| 首轮修复后仍剩 9 个散落文件 | `takeown /F <file> /D Y` 缺少 `/R` 时 `/D` 非法，单文件分支静默失败 | `WindowsAclTool` 统一为 `/F … /R /D Y`，目录目标全部可删（新增 2 934 个文件）；System32 下 9 个受系统保护的硬链接仍被跳过，属设计内残留（见「已知限制」） |
| 输出版本名变成 `Windows 10 Home` | wimlib capture 从离线注册表 `PRODUCTNAME` 自动写 `DISPLAYNAME`，而检查器优先读 `DISPLAYNAME` | `SetImageProperties` 回写三项名称属性，中文版本名同样有效 |
| 精简结束留下 6.6 GB 临时目录 | 只读属性 + TrustedInstaller ACL 双重阻挡 | 提权夺权清理 + `--trimcleanup` 维护命令 |

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
| `--deeptest [临时目录]` | 递归提取 / 深度清理 / MBR+GPT 空白 VHDX。系统盘空间不足时可在参数里指定数据盘目录作夹具根目录 | `basalt_deeptest.txt` |
| `--deepui [--light]` | 新增窗口渲染（暗/亮） | `basalt_deepui*.txt` |
| `--previewui [--light]` | 三个预览窗口渲染（暗/亮） | `basalt_previewui*.txt` |
| `--isotest` | 合成夹具：WIM XML 解析 / sources 定位 / 映像大小 / ESD 降级 / `ReadHead` 早停 | `basalt_isotest.txt` |
| `--isoui [--light]` | 夹具 ISO 检查器窗口渲染（暗/亮） | `basalt_isoui*.txt` |
| `--isoreal <iso>` | **真实 Windows ISO**：尾部 XML 偏移、UDF 可寻址读取、版本列表、耗时 | `basalt_isoreal.txt` |
| `--isorealui <iso> [--light]` | 真实 ISO 检查器窗口渲染与截图（暗/亮） | `basalt_isorealui*.txt` / `basalt_iso_real*.png` |
| `--trimtest` | **端到端精简**：wimlib capture / export + CDBuilder 真 ISO 夹具 + TrimWorkflow 全链路 + 重新 apply 验证删除结果 + 取消 / 防覆盖 / 源 ISO 哈希；另含只读文件清理对照、中文版本名污染回写、输出 ISO 版本名一致性、工作目录回收 | `basalt_trimtest.txt` |
| `--trimui [--light]` | 精简后的检查器与阶段进度窗口渲染（暗/亮） | `basalt_trimui*.txt` / `basalt_trim_*.png` |
| `--trimreal <iso>` | 真实 ISO **干跑**：定位 WIM 物理 extent、空间预检，不写任何文件 | `basalt_trimreal.txt` |
| `--trimfull <iso> [--out <path>]` | 真实 ISO **完整实跑**：真的写出精简版 ISO（需约 27 GB 临时空间）；源盘被历史产物占满时可用 `--out` 把输出改写到数据盘 | `basalt_trimfull.txt` |
| `--trimcleanup <dir>` | 维护命令：清理历史任务残留的只读 / TrustedInstaller ACL 工作目录（需提权，程序 manifest 已声明） | `basalt_trimcleanup.txt` |

`--isoreal` / `--isorealui` / `--trimreal` / `--trimfull` 需要本机有一份真实 Windows 安装 ISO；路径中的 `--light` 等开关可出现在路径前或后。不带 ISO 时其余自测不依赖该文件，全部使用自建夹具。`--trimtest` 的夹具默认落在剩余空间最大的固定盘（`BASALT_TEST_ROOT` 可显式指定），避免系统盘瞬时写满干扰 wimlib。

渲染类自测会同时把截图写到 `%TEMP%\basalt_*.png`。当前 Release 基线：**本轮全套回归 0 失败** —— 新增 `--trimtest` 62 PASS / 0 FAIL、`--trimui` 暗/亮各 22 PASS、`--trimreal` 11 PASS、`--trimfull` 真实 Win11 ISO 完整实跑 0 失败；原有 `--deeptest` 36 PASS、`--isotest` 30 PASS、`--isoui` 暗/亮各 16 PASS、`--isoreal` 24 PASS、`--isorealui` 暗/亮各 10 PASS，以及 `--clitest` / `--previewtest` / `--previewui` / `--deepui` / `--selftest` 全绿；Debug / Release 均 0 警告 0 错误。

真实 Win11 22H2 ISO 完整实跑（5.34 GiB）约 11~13 分钟：`install.wim` 从 4.59 GB 精简到约 4.02 GB，删除 4 110 个文件 / 1.83 GB，稀疏写入让输出 ISO 实际少占约 580 MB；源 ISO 哈希与时间戳不变，输出 ISO 可被检查器和 7-Zip 重新解析，版本名保持用户选中的「Windows 11 家庭版」。

> 注意：`--deeptest` 会创建 1 GB 动态 VHDX 并预分配 NTFS 元数据。若系统盘空间不足（例如 C: 仅剩几十 MB），用 `--deeptest D:\some\dir` 把夹具根目录放到数据盘；夹具结束自行删除。应用带 `requireAdministrator`，自动提权会丢弃父进程的自定义 `TEMP`，所以重定向环境变量无效，须走该参数。

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
├── Views/                 9 个窗口 × XAML + code-behind
├── ViewModels/            MainViewModel / BreadcrumbItem / ViewModelBase
├── Models/                FileItem.cs（FileItem / FileMetadata / FolderNode / FileItemFormatter）
├── Services/              23 个业务服务（引擎、预览、清理、VHDX、CLI、Chert、WIM 元数据、ISO 随机读取 / 检查、WIM 工具、Windows ACL 夺权、精简流水线…）
├── Common/                IconProvider / TempFileRegistry / RangeObservableCollection
├── Native/                virtdisk P/Invoke 与诊断
├── Properties/            AssemblyInfo.cs
└── Tests/                 14 个自测 / 渲染测试
```

仓库根另有 `tools/wimlib-x64/`：`wimlib-imagex.exe` + `libwim-15.dll` + 官方许可证与 `VERSION.txt`，由 `src/Basalt.csproj` 复制到输出目录。

关键文件：`Services/IVirtualDisk.cs`（引擎抽象，含 `ReadHead` / `ReadRange`）、`Services/DiskEngineFactory.cs`（分流）、`Services/PreviewDispatcher.cs`（预览分发与嗅探）、`Services/IsoRandomAccess.cs`（UDF / ISO9660 可寻址读取）、`Services/WimMetadataReader.cs`（WIM 头 + XML 两阶段解析）、`Services/IsoExtentService.cs`（WIM 物理 extent 定位 / 原位替换 / 稀疏写入）、`Services/WimTool.cs`（wimlib / DISM 进程封装）、`Services/WindowsAclTool.cs`（TrustedInstaller ACL 夺权）、`Services/ImageTrimService.cs`（精简目标收集，复用 CleanupService）、`Services/TrimWorkflow.cs`（六阶段编排与取消）、`ViewModels/MainViewModel.cs`（UI 状态与命令）、`Services/CliMount.cs`（CLI）。

`.gitattributes` 统一 `eol=lf`，图片/可执行文件按二进制处理。

---

## 10. 架构要点

- **统一流抽象**：`IVirtualDisk.OpenRead` 屏蔽三种引擎的物理差异，所有内部预览只认 `Stream`。容器引擎读取时不会重新 `OpenRead`（嗅探消耗的头部通过 `prefix` 拼回），避免重复启动 7z 进程。
- **编码**：启动时注册 `CodePagesEncodingProvider`；7z 列目录加 `-sccUTF-8`，原始字节按 UTF-8 / GB18030 双解码，中文目录名不乱码。
- **图标**：VHD 用 `ExtractAssociatedIcon` 取真实图标，VMDK / 容器按扩展名取关联图标，句柄统一 `DestroyIcon` 释放。
- **主题**：`DynamicResource` 绑定 `Themes/*.xaml`，预览窗口跟随主窗口亮暗；选中行暗色 `#334F8CFF`、亮色 `#E5E7EB`。
- **临时文件**：VMDK / 容器交给外部程序前落地到 `%TEMP%\BasaltOpen`，退出时统一清理；精简工作目录在 `%TEMP%\Basalt_Wim_Work`（可改），任务结束自动删除，遇到 TrustedInstaller ACL 时由提权进程 `takeown` / `icacls` 夺权后再删。
- **WIM 进程封装**：`WimTool.Run` 按字符流抽干 stdout/stderr，遇 `\r` 或 `\n` 都切行（wimlib 的进度用 `\r` 刷新），解析百分比；`CancellationToken` 注册回调 Kill 整棵进程树，退出码非 0 时带上最后几行输出抛错。
- **ISO 原位替换**：`IsoExtentService` 用扇区对齐的 `MSWIM` 魔数定位 extent，再用 WIM 头的 XML 绝对偏移排除 `boot.wim`，并用 UDF 文件流抽查连续性；替换时保持 UDF 元数据不动，缩短部分用 `FSCTL_SET_SPARSE` 做成 NTFS 空洞。写流释放后才回读复核，避免 `FileShare.None` 自锁。

---

## 11. 已知限制

| 项 | 说明 |
|---|---|
| ext4 / Linux 分区 | DiscUtils 不支持，只能列出分区并标注不可读 |
| 权限 | VHD 挂载需要管理员。非提权环境下 `AttachVirtualDisk` 可能成功但 CIM 盘符分配失败，CLI daemon 全流程无法实机验证 |
| `chert://` | 当前版本 Chert 未注册该 URL Scheme，按钮走 GUI 回退 |
| 深度清理 | 只读引擎（VMDK / 容器 / 只读 VHD）只扫描统计，不提供删除 |
| 归档转 VHDX | 仅 `.zip` / `.7z` 启用；NTFS 元数据会预分配，实际占用远大于归档体积 |
| ISO 检查器 | `install.esd` 的 LZMS 压缩 XML 不解析，自动降级为文件清单；分卷 `install.swm` 未纳入识别 |
| 精简适用范围 | 需要单个连续的 UDF / ISO9660 extent；非光盘容器、分卷 WIM、多 extent 布局会明确拒绝，绝不产出损坏的 ISO。输出 ISO **逻辑尺寸保持不变**（UDF 元数据不改写），只在 NTFS 上通过稀疏空洞减少实际占用；新 WIM 若比原 extent 大则中止 |
| 精简耗时 | 5.34 GiB 的 Win11 ISO 完整实跑约 11~13 分钟。`wimlib apply` 还原的系统文件带 TrustedInstaller ACL，工作目录回收要提权 `takeown /R` + `icacls /T` 逐文件处理，这一步可能耗时数分钟，进度条会停在「正在回收临时空间」；这是为了保证输出 WIM 的安全描述符不被改写而刻意付出的代价 |
| 精简残留 | 真实 Win11 映像的 `Windows\System32` 下 9 个 `SecurityHealth*.dll/exe` 是受系统保护的硬链接，夺权后仍无法删除（跳过数 2 943 → 9）。Defender 目录本体与其余散落组件均已移除，不影响映像解析与安装流程；`--trimfull` 断言允许 ≤10 个此类跳过 |
| 精简版本数 | 「保留其它版本」默认开启时用 `export` 逐个追加，精简后的版本排到索引 #1；关闭时新 `install.wim` 只含 1 个映像。`DISPLAYNAME` 由 `SetImageProperties` 回写为选中的版本名，避免 wimlib 用离线注册表的旧 `PRODUCTNAME` 覆盖 |
| WIM 元数据保真 | wimlib 在非提权环境下无法完整还原 NTFS 安全描述符（会有 warning）；Basalt 本身 `requireAdministrator`，正常启动不受影响。清理只删文件与目录，不编辑离线注册表，Defender / Edge 的服务与策略项仍会保留在注册表里 |
| ISO 实机 | 已用 5.34 GiB 的 Win11 22H2 官方 ISO 验证检查器（6 个版本 / 186 个 sources 文件 / 515 ms）、WIM 物理 extent 定位与 `wimlib verify`（9 GiB 文件数据）、以及 `wimlib dir` 的 132 400 条精简目标路径。完整 apply / capture 实跑见 `--trimfull` 报告：输出 ISO 可被检查器与 7-Zip 正常解析、版本名与用户选择一致、源 ISO 哈希不变 |
| 遗留 | `src/Tests/` 下文件 namespace 仍为 `Basalt.Services`（重组目录时未改，避免牵动 `App.xaml.cs` 引用） |

---

## 12. 开发约定

- 提交前至少跑 `dotnet build -c Release` 与 `--previewtest` / `--deeptest` / `--isotest` / `--trimtest`，UI 改动另跑 `--deepui` / `--previewui` / `--isoui` / `--trimui` 暗亮两轮；本机有真实 Windows ISO 时追加 `--isoreal <iso>` / `--isorealui <iso> --light` / `--trimreal <iso>`，需要完整验证时再跑 `--trimfull <iso>`。若上一次实跑留下了只读工作目录，先用 `--trimcleanup <dir>` 回收，再跑新的精简。
- 精简链路涉及三种 Windows 文件权限障碍：`ReadOnly` 属性（`clearReadOnly`）、TrustedInstaller ACL（`takeown` / `icacls`，仅对临时副本）、reparse point（跳过）。改清理逻辑时别只修第一种。
- 修改精简规则时务必用 `wimlib-imagex dir <install.wim> 1` 对照真实映像的目录名（Defender 是散落文件、Edge 有四个目录、驱动包命名 `<inf>.inf_amd64_<hash>`），不要凭印象写路径。
- 已配置双推：一次 `git push origin main` 同时更新 CNB 主仓与 GitHub 镜像，两端 `main` 保持同一 hash。两个平台分别需要各自的令牌（CNB 需 `repo-code:rw`）；凭据只通过一次性 HTTP header 传入，**不要写进 `.git/config` 或提交到仓库**。
- 新增磁盘引擎请实现 `IVirtualDisk` 并在 `DiskEngineFactory` 注册；新增自测请同时更新本文件的命令表。
