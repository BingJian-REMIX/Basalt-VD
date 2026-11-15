using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Basalt.Services;

/// <summary>
/// ISO 检查器自测：纯托管 WIM XML 解析、sources 定位、install.wim/esd 大小、
/// 版本列表、ESD 降级、头部预读早停，以及窗口在暗/亮主题下的渲染。
/// 夹具全部自建（合成 WIM 头 + 7z 打包），不依赖真实 Windows ISO 或管理员权限。
/// </summary>
public static class IsoInspectorSelfTest
{
    private const string FixtureXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <WIM>
          <TOTALBYTES>5368709120</TOTALBYTES>
          <IMAGE INDEX="1">
            <NAME>Windows 11 Pro</NAME>
            <DESCRIPTION>Windows 11 Pro</DESCRIPTION>
            <DISPLAYNAME>Windows 11 专业版</DISPLAYNAME>
            <TOTALBYTES>16106127360</TOTALBYTES>
            <WINDOWS>
              <ARCH>9</ARCH>
              <PRODUCTNAME>Microsoft Windows Operating System</PRODUCTNAME>
              <EDITIONID>Professional</EDITIONID>
              <VERSION><MAJOR>10</MAJOR><MINOR>0</MINOR><BUILD>22631</BUILD><SPBUILD>2861</SPBUILD></VERSION>
            </WINDOWS>
          </IMAGE>
          <IMAGE INDEX="2">
            <NAME>Windows 11 Home</NAME>
            <DESCRIPTION>Windows 11 Home</DESCRIPTION>
            <DISPLAYNAME>Windows 11 家庭版</DISPLAYNAME>
            <TOTALBYTES>15032385536</TOTALBYTES>
            <WINDOWS>
              <ARCH>12</ARCH>
              <PRODUCTNAME>Microsoft Windows Operating System</PRODUCTNAME>
              <EDITIONID>Core</EDITIONID>
              <VERSION><MAJOR>10</MAJOR><MINOR>0</MINOR><BUILD>22631</BUILD></VERSION>
            </WINDOWS>
          </IMAGE>
        </WIM>
        """;

    private static void W(StringBuilder sb, string s) => sb.AppendLine(s);

    public static string Run()
    {
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(bool ok, string label, string detail = "")
        {
            W(sb, $"   [{(ok ? "PASS" : "FAIL")}] {label}{(detail.Length > 0 ? " — " + detail : "")}");
            if (ok) pass++; else fail++;
        }

        W(sb, "=== Basalt ISO 检查器自测 ===");
        W(sb, "");

        string stage = Path.Combine(Path.GetTempPath(), $"BasaltIsoTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        try
        {
            string? sevenZip = ContainerDetector.SevenZipPath;
            Check(sevenZip != null, "7-Zip 可用");
            if (sevenZip == null) return Finish();

            byte[] wim = BuildFakeWim(FixtureXml, compressedXml: false);

            W(sb, "-- 1. 纯托管 WIM 头 / XML 解析 --");
            var direct = WimMetadataReader.Read(wim);
            Check(direct.Success, "WIM 头 + XML 解析成功", direct.Note ?? "");
            Check(direct.Images.Count == 2, "解析出 2 个映像", direct.Images.Count.ToString());
            Check(direct.Images[0].Name == "Windows 11 专业版", "DISPLAYNAME 优先作为版本名", direct.Images[0].Name);
            Check(direct.Images[0].Version == "10.0.22631", "VERSION 三段拼接", direct.Images[0].Version);
            Check(direct.Images[0].Architecture == "x64", "ARCH=9 映射 x64", direct.Images[0].Architecture);
            Check(direct.Images[1].Architecture == "ARM64", "ARCH=12 映射 ARM64", direct.Images[1].Architecture);
            Check(direct.Compression == "LZX", "压缩类型识别", direct.Compression);
            Check(direct.Images[0].SizeText.Length > 0, "TOTALBYTES 已格式化", direct.Images[0].SizeText);
            Check(!WimMetadataReader.Read(new byte[64]).Success, "非 WIM 输入安全降级");
            var esdHead = BuildFakeWim(FixtureXml, compressedXml: true);
            var esdDirect = WimMetadataReader.Read(esdHead);
            Check(!esdDirect.Success && (esdDirect.Note ?? "").Contains("压缩"),
                "压缩 XML 资源明确降级", esdDirect.Note ?? "");

            W(sb, "");
            W(sb, "-- 2. 容器链路：sources / install.wim / 版本列表 --");
            string wimZip = MakeFixture(stage, "wim", "install.wim", wim, sevenZip);
            using (var disk = new ContainerDiskService(wimZip))
            {
                Check(IsoInspectorService.FindSourcesPath(disk) == "sources", "自动定位 sources 目录");
                var r = IsoInspectorService.Inspect(disk);
                Check(r.InstallImageName == "install.wim", "识别 install.wim", r.InstallImageDisplay);
                Check(r.InstallImageSize == wim.Length, "install.wim 大小正确", $"{r.InstallImageSize} / {wim.Length}");
                Check(r.InstallImageSizeText.Length > 0, "大小已格式化", r.InstallImageSizeText);
                Check(r.Files.Count == 3, "sources 文件清单 3 项", r.Files.Count.ToString());
                Check(r.Files.Any(f => f.IsInstallImage && f.Name == "install.wim"), "安装映像被高亮标记");
                Check(r.Images.Count == 2, "UI 版本列表 2 项", r.Images.Count.ToString());
                Check(r.Wim?.Success == true, "WIM 元数据链路成功", r.Wim?.Summary ?? "");
                Check(r.Error == null, "无降级提示", r.Error ?? "");
            }

            W(sb, "");
            W(sb, "-- 3. install.esd / 压缩元数据降级 --");
            string esdZip = MakeFixture(stage, "esd", "install.esd", esdHead, sevenZip);
            using (var disk2 = new ContainerDiskService(esdZip))
            {
                var r2 = IsoInspectorService.Inspect(disk2);
                Check(r2.InstallImageName == "install.esd", "识别 install.esd", r2.InstallImageDisplay);
                Check(r2.InstallImageSize == esdHead.Length, "install.esd 大小正确");
                Check(r2.Images.Count == 0, "压缩元数据不强行解析", r2.Wim?.Note ?? "");
                Check(r2.Files.Count == 3, "降级后仍给出文件清单", r2.Files.Count.ToString());
                Check(r2.Error != null, "给出降级提示", r2.Error ?? "");
            }

            W(sb, "");
            W(sb, "-- 4. 头部预读早停（不把大 WIM 全量入内存）--");
            using (var disk3 = new ContainerDiskService(wimZip))
            {
                byte[] head16 = disk3.ReadHead("sources\\install.wim", 16);
                Check(head16.Length == 16 && head16[0] == 'M' && head16[1] == 'S',
                    "ReadHead 只取前 16 字节并终止 7z", $"{head16.Length} 字节");
                byte[] all = disk3.ReadHead("sources\\install.wim", 1 << 20);
                Check(all.Length == wim.Length, "预读上限大于文件时读到 EOF", $"{all.Length} / {wim.Length}");
            }

            W(sb, "");
            W(sb, "-- 5. 非安装镜像 --");
            string plainZip = MakeFixture(stage, "plain", "readme.txt", Encoding.UTF8.GetBytes("not a wim"), sevenZip);
            using (var disk4 = new ContainerDiskService(plainZip))
            {
                var r4 = IsoInspectorService.Inspect(disk4);
                Check(r4.InstallImagePath == null, "不误判安装映像");
                Check(r4.Error != null, "给出明确提示", r4.Error ?? "");
                Check(r4.Files.Count > 0, "仍展示文件清单");
            }
        }
        catch (Exception ex)
        {
            Check(false, "自测执行异常", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { Directory.Delete(stage, recursive: true); } catch { /* 占用时留给系统清理 */ }
        }

        return Finish();

        string Finish()
        {
            W(sb, "");
            W(sb, $"合计: {pass} PASS / {fail} FAIL / {pass + fail} 项");
            return sb.ToString();
        }
    }

    public static string RunUi(bool lightTheme)
    {
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(bool ok, string label, string detail = "")
        {
            W(sb, $"   [{(ok ? "PASS" : "FAIL")}] {label}{(detail.Length > 0 ? " — " + detail : "")}");
            if (ok) pass++; else fail++;
        }

        W(sb, "=== Basalt ISO 检查器 UI 渲染自测 ===");
        W(sb, $"主题: {(lightTheme ? "亮色" : "暗色")}");
        W(sb, "");

        string stage = Path.Combine(Path.GetTempPath(), $"BasaltIsoUi_{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        var windows = new List<Window>();
        try
        {
            if (lightTheme) ((App)Application.Current).ToggleTheme();

            string? sevenZip = ContainerDetector.SevenZipPath;
            Check(sevenZip != null, "7-Zip 可用");
            if (sevenZip != null)
            {
                string zip = MakeFixture(stage, "ui", "install.wim",
                    BuildFakeWim(FixtureXml, compressedXml: false), sevenZip);
                using var disk = new ContainerDiskService(zip);

                var win = new IsoInspectorWindow(disk, "fixture.iso");
                windows.Add(win);
                win.Show();
                win.UpdateLayout();

                PumpUntil(() => win.Result != null, 8000);
                Check(win.Result != null, "检查器异步加载完成");

                var versions = win.FindName("VersionList") as ItemsControl;
                var files = win.FindName("FileList") as ItemsControl;
                var sizeText = win.FindName("SizeText") as TextBlock;
                var metaText = win.FindName("MetaText") as TextBlock;
                var sourcesText = win.FindName("SourcesText") as TextBlock;
                var extract = win.FindName("ExtractButton") as Button;
                var apply = win.FindName("ApplyTrimButton") as Button;
                var defender = win.FindName("DefenderBox") as CheckBox;
                var edge = win.FindName("EdgeBox") as CheckBox;
                var driver = win.FindName("DriverBox") as CheckBox;
                var trimSummary = win.FindName("TrimSummary") as TextBlock;

                Check(sourcesText?.Text == "sources", "概览显示 sources 路径", sourcesText?.Text ?? "");
                Check(sizeText?.Text.Length > 0 && sizeText.Text != "—", "概览显示安装映像大小", sizeText?.Text ?? "");
                Check((metaText?.Text ?? "").Contains("已解析"), "概览显示 WIM 元数据摘要", metaText?.Text ?? "");
                Check(versions?.Items.Count == 2, "版本列表渲染 2 行", versions?.Items.Count.ToString() ?? "");
                Check(files?.Items.Count == 3, "sources 文件清单渲染 3 行", files?.Items.Count.ToString() ?? "");
                Check(extract?.IsEnabled == true, "提取按钮可用");
                Check(defender != null && edge != null && driver != null, "三个精简勾选项存在");
                Check(apply?.IsEnabled == false, "应用精简按钮为禁用占位");

                if (defender != null)
                {
                    defender.IsChecked = true;
                    Check((trimSummary?.Text ?? "").Contains("已选 1 项"), "勾选后摘要更新", trimSummary?.Text ?? "");
                    Check(apply?.IsEnabled == false, "勾选仍不启用底层修改");
                }

                string shot = Shot(win, lightTheme ? "basalt_iso_ui_light.png" : "basalt_iso_ui.png");
                Check(shot.Length > 0, "检查器截图已生成", shot);
            }
        }
        catch (Exception ex)
        {
            Check(false, "UI 自测执行异常", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            foreach (var w in windows) { try { w.Close(); } catch { } }
            try { Directory.Delete(stage, recursive: true); } catch { }
        }

        W(sb, "");
        W(sb, $"合计: {pass} PASS / {fail} FAIL / {pass + fail} 项");
        return sb.ToString();
    }

    // ==================== 夹具 ====================

    /// <summary>合成最小 WIM：208 字节头 + 未压缩 UTF-16LE XML 资源。</summary>
    private static byte[] BuildFakeWim(string xml, bool compressedXml)
    {
        byte[] xmlBytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(xml)).ToArray();
        var file = new byte[208 + xmlBytes.Length + 32];
        byte[] magic = { 0x4D, 0x53, 0x57, 0x49, 0x4D, 0, 0, 0 };   // "MSWIM\0\0\0"
        Buffer.BlockCopy(magic, 0, file, 0, 8);
        Put32(file, 0x08, 208);                       // hdr_size
        Put32(file, 0x0C, 0x00010D00);                // version
        Put32(file, 0x10, 0x00000002 | 0x00040000);   // COMPRESSION + LZX
        Put32(file, 0x14, 32768);                     // chunk size
        Put32(file, 0x2C, 2);                         // image count

        // rhXmlData @ 0x48：size_in_wim[7] + flags[1] + offset[8] + uncompressed_size[8]
        Put64(file, 0x48, (ulong)xmlBytes.Length);
        file[0x48 + 7] = (byte)(0x02 | (compressedXml ? 0x04 : 0));   // METADATA (+ COMPRESSED)
        Put64(file, 0x48 + 8, 208);
        Put64(file, 0x48 + 16, (ulong)xmlBytes.Length);
        Buffer.BlockCopy(xmlBytes, 0, file, 208, xmlBytes.Length);
        return file;
    }

    /// <summary>在独立子目录里造 sources + 安装映像，用 7z 打成 zip 作为容器夹具。</summary>
    private static string MakeFixture(string stage, string name, string installName, byte[] image, string sevenZip)
    {
        string root = Path.Combine(stage, name);
        string sources = Path.Combine(root, "sources");
        Directory.CreateDirectory(sources);
        File.WriteAllBytes(Path.Combine(sources, installName), image);
        File.WriteAllBytes(Path.Combine(sources, "boot.wim"), new byte[1234]);
        File.WriteAllText(Path.Combine(sources, "setup.exe"), "fixture");
        string zip = Path.Combine(root, "fixture.zip");
        if (!MakeZip(sevenZip, zip, root, "sources"))
            throw new InvalidOperationException("7z 打包夹具失败");
        return zip;
    }

    private static bool MakeZip(string sevenZip, string zipPath, string workDir, params string[] sources)
    {
        var psi = new ProcessStartInfo(sevenZip)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("a");
        psi.ArgumentList.Add(zipPath);
        psi.ArgumentList.Add("-r");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-bso0");
        psi.ArgumentList.Add("-bsp0");
        foreach (string s in sources) psi.ArgumentList.Add(s);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 7-Zip。");
        proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit(120000);
        return proc.ExitCode == 0 && File.Exists(zipPath);
    }

    private static void Put32(byte[] b, int off, uint v)
        => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(off, 4), v);

    private static void Put64(byte[] b, int off, ulong v)
        => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(off, 8), v);

    /// <summary>推进 WPF 消息泵，等异步检查结果回到 UI 线程。</summary>
    private static void PumpUntil(Func<bool> done, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < timeoutMs)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background,
                (s, e) => { ((DispatcherTimer)s!).Stop(); frame.Continue = false; },
                Application.Current.Dispatcher);
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }

    private static string Shot(Window win, string fileName)
    {
        win.Show();
        win.UpdateLayout();
        win.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        int w = Math.Max(1, (int)Math.Ceiling(win.ActualWidth));
        int h = Math.Max(1, (int)Math.Ceiling(win.ActualHeight));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(win);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        string path = Path.Combine(Path.GetTempPath(), fileName);
        using var fs = File.Create(path);
        encoder.Save(fs);
        return path;
    }
}
