using System.IO;
using System.Text;
using System.Windows;

namespace Basalt;

public partial class App : Application
{
    public const string ThemeKey = "Basalt.Theme";

    /// <summary>无界面模式（自测/诊断命令）：禁止弹 MessageBox，否则会阻塞或抛异常。</summary>
    public static bool Headless { get; set; }

    public bool IsDarkTheme { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // GB18030（GBK 超集）用于老归档中文名 / 无 BOM 中文文本的兜底解码，必须先注册提供器
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // CLI 自动化接口（供 Chert 后台调用）：进程可常驻，故必须在其它诊断分支之前处理
        var args = e.Args;
        bool cli = args.Length > 0 &&
                   (args[0].Equals("mount", StringComparison.OrdinalIgnoreCase) ||
                    args[0].Equals("stop", StringComparison.OrdinalIgnoreCase));

        // 诊断模式：Basalt.exe --diag <vhd路径>，逐步执行 P/Invoke 并输出结果
        Headless = cli || args.Any(a => a is "--uitest" or "--rendertest" or "--featuretest" or "--containertest" or "--containerui" or "--nestedtest" or "--mounttest" or "--diag" or "--selftest" or "--previewtest" or "--vhdxtest" or "--clitest" or "--previewui" or "--deeptest" or "--deepui" or "--isotest" or "--isoui" or "--isoreal" or "--isorealui" or "--trimtest" or "--trimui" or "--trimreal" or "--trimfull" or "--trimcleanup");
        if (cli)
        {
            int code = args[0].Equals("mount", StringComparison.OrdinalIgnoreCase)
                ? Services.CliMount.RunMount(args)
                : Services.CliMount.RunStop(args);
            Shutdown(code);
            return;
        }
        int i = Array.IndexOf(args, "--diag");
        int st = Array.IndexOf(args, "--selftest");
        if (st >= 0)
        {
            string report = Native.VhdDiag.SelfTest();
            WriteReport(report, "basalt_selftest.txt");
            if (!Headless)
                MessageBox.Show(report + $"\n完整结果已写入：{Path.Combine(Path.GetTempPath(), "basalt_selftest.txt")}", "Basalt — 自检结果");
            Shutdown();
            return;
        }
        if (i >= 0 && i + 1 < args.Length)
        {
            string report = Native.VhdDiag.Run(args[i + 1]);
            WriteReport(report, "basalt_diag.txt");
            if (!Headless)
                MessageBox.Show(report + $"\n完整结果已写入：{Path.Combine(Path.GetTempPath(), "basalt_diag.txt")}", "Basalt — 诊断结果");
            Shutdown();
            return;
        }
        int mt = Array.IndexOf(args, "--mounttest");
        if (mt >= 0 && mt + 1 < args.Length)
        {
            string report = Services.MountTest.Run(args[mt + 1]);
            WriteReport(report, "basalt_mounttest.txt");
            Shutdown();
            return;
        }
        int ut = Array.IndexOf(args, "--uitest");
        if (ut >= 0 && ut + 1 < args.Length)
        {
            // UI 数据链自测：验证左侧树 / 右侧列表的集合通知与交互路径（无需人工点窗口）
            string report = Services.UiSelfTest.Run(args[ut + 1]);
            WriteReport(report, "basalt_uitest.txt");
            Shutdown();
            return;
        }
        int wt = Array.IndexOf(args, "--rendertest");
        if (wt >= 0 && wt + 1 < args.Length)
        {
            // 渲染级自测：真实创建窗口并遍历视觉树，确认节点已画出（无需 resize）
            bool light = Array.IndexOf(args, "--light") >= 0;
            string report = Services.WpfRenderTest.Run(args[wt + 1], light);
            WriteReport(report, light ? "basalt_rendertest_light.txt" : "basalt_rendertest.txt");
            Shutdown();
            return;
        }
        int ft = Array.IndexOf(args, "--featuretest");
        if (ft >= 0 && ft + 1 < args.Length)
        {
            // 功能级自测：..上级 / 面包屑 / 元数据 / 图标 / 状态栏计数 / 提取落盘
            string report = Services.FeatureSelfTest.Run(args[ft + 1]);
            WriteReport(report, "basalt_featuretest.txt");
            Shutdown();
            return;
        }
        int ct = Array.IndexOf(args, "--containertest");
        if (ct >= 0 && ct + 1 < args.Length)
        {
            // 容器自测：zip/iso/rar/7z/SFX 的识别、列举、提取
            string report = Services.ContainerSelfTest.Run(args[ct + 1]);
            WriteReport(report, "basalt_containertest.txt");
            Shutdown();
            return;
        }
        int crt = Array.IndexOf(args, "--containerui");
        if (crt >= 0 && crt + 1 < args.Length)
        {
            // 容器 UI 渲染自测：真实窗口里展开浏览归档并截图
            string report = Services.ContainerRenderTest.Run(args[crt + 1]);
            WriteReport(report, "basalt_containerui.txt");
            Shutdown();
            return;
        }
        int nct = Array.IndexOf(args, "--nestedtest");
        if (nct >= 0 && nct + 1 < args.Length)
        {
            // 虚拟盘内容器链路：VMDK/VHD 内的 zip/iso → 实体化 → 识别 → 浏览 → 提取
            string report = Services.NestedContainerSelfTest.Run(args[nct + 1]);
            WriteReport(report, "basalt_nestedtest.txt");
            Shutdown();
            return;
        }
        int pt = Array.IndexOf(args, "--previewtest");
        if (pt >= 0)
        {
            // 预览分发 / 7z 编码 / 归档转 VHDX 全链路自测（可附带一个外部镜像路径）
            string report = Services.PreviewSelfTest.Run(pt + 1 < args.Length ? args[pt + 1] : null);
            WriteReport(report, "basalt_previewtest.txt");
            Shutdown();
            return;
        }
        int vt = Array.IndexOf(args, "--vhdxtest");
        if (vt >= 0 && vt + 1 < args.Length)
        {
            string report = Services.PreviewSelfTest.RunVhdx(args[vt + 1]);
            WriteReport(report, "basalt_vhdxtest.txt");
            Shutdown();
            return;
        }
        int cl = Array.IndexOf(args, "--clitest");
        if (cl >= 0)
        {
            string report = Services.CliSelfTest.Run();
            WriteReport(report, "basalt_clitest.txt");
            Shutdown();
            return;
        }
        int dt = Array.IndexOf(args, "--deeptest");
        if (dt >= 0)
        {
            // 递归提取 / 深度清理 / 空白 VHDX（MBR+GPT）全链路自测
            string report = Services.DeepFeatureSelfTest.Run(PathArg(args, dt + 1));
            WriteReport(report, "basalt_deeptest.txt");
            Shutdown();
            return;
        }
        int du = Array.IndexOf(args, "--deepui");
        if (du >= 0)
        {
            bool light = Array.IndexOf(args, "--light") >= 0;
            string report = Services.DeepRenderTest.Run(light);
            WriteReport(report, light ? "basalt_deepui_light.txt" : "basalt_deepui.txt");
            Shutdown();
            return;
        }
        int isot = Array.IndexOf(args, "--isotest");
        if (isot >= 0)
        {
            // ISO 检查器数据层：sources 定位 / install.wim 大小 / WIM XML 版本列表 / ESD 降级
            string report = Services.IsoInspectorSelfTest.Run();
            WriteReport(report, "basalt_isotest.txt");
            Shutdown();
            return;
        }
        int isou = Array.IndexOf(args, "--isoui");
        if (isou >= 0)
        {
            bool light = Array.IndexOf(args, "--light") >= 0;
            string report = Services.IsoInspectorSelfTest.RunUi(light);
            WriteReport(report, light ? "basalt_isoui_light.txt" : "basalt_isoui.txt");
            Shutdown();
            return;
        }
        int ir = Array.IndexOf(args, "--isoreal");
        if (ir >= 0)
        {
            // 真实 Windows ISO 实机验证：sources / install.wim 大小 / 尾部 XML 版本列表 / 耗时
            string report = Services.IsoInspectorSelfTest.RunReal(PathArg(args, ir + 1));
            WriteReport(report, "basalt_isoreal.txt");
            Shutdown();
            return;
        }
        int iru = Array.IndexOf(args, "--isorealui");
        if (iru >= 0)
        {
            // 用真实 ISO 渲染检查器窗口并截图
            bool light = Array.IndexOf(args, "--light") >= 0;
            string report = Services.IsoInspectorSelfTest.RunRealUi(PathArg(args, iru + 1), light);
            WriteReport(report, light ? "basalt_isorealui_light.txt" : "basalt_isorealui.txt");
            Shutdown();
            return;
        }
        int tt = Array.IndexOf(args, "--trimtest");
        if (tt >= 0)
        {
            // 一键精简端到端：wimlib capture/export + CDBuilder ISO + TrimWorkflow 全链路
            string report = Services.TrimSelfTest.Run();
            WriteReport(report, "basalt_trimtest.txt");
            Shutdown();
            return;
        }
        int tu = Array.IndexOf(args, "--trimui");
        if (tu >= 0)
        {
            bool light = Array.IndexOf(args, "--light") >= 0;
            string report = Services.TrimSelfTest.RunUi(light);
            WriteReport(report, light ? "basalt_trimui_light.txt" : "basalt_trimui.txt");
            Shutdown();
            return;
        }
        int tf = Array.IndexOf(args, "--trimfull");
        if (tf >= 0)
        {
            // 真实 ISO 完整实跑：会写出一个全新的精简版 ISO，需要足够的临时磁盘空间。
            // 可选 --out <iso路径> 把输出改写到空间更充裕的盘（源 ISO 所在盘可能已满）。
            int oi = Array.IndexOf(args, "--out");
            string outPath = oi >= 0 ? PathArg(args, oi + 1) : "";
            string report = Services.TrimSelfTest.RunFull(PathArg(args, tf + 1), outPath);
            WriteReport(report, "basalt_trimfull.txt");
            Shutdown();
            return;
        }
        int tr = Array.IndexOf(args, "--trimreal");
        if (tr >= 0)
        {
            // 真实 ISO 干跑：只定位 extent 与预检空间，绝不写入任何文件
            string report = Services.TrimSelfTest.RunReal(PathArg(args, tr + 1));
            WriteReport(report, "basalt_trimreal.txt");
            Shutdown();
            return;
        }
        int tc = Array.IndexOf(args, "--trimcleanup");
        if (tc >= 0)
        {
            // 维护命令：清理历史精简任务残留的工作目录（TrustedInstaller ACL 需要提权处理）
            string report = Services.TrimWorkflow.CleanupWorkDir(PathArg(args, tc + 1));
            WriteReport(report, "basalt_trimcleanup.txt");
            Shutdown();
            return;
        }
        int pu = Array.IndexOf(args, "--previewui");
        if (pu >= 0)
        {
            bool light = Array.IndexOf(args, "--light") >= 0;
            string report = Services.PreviewRenderTest.Run(light);
            WriteReport(report, light ? "basalt_previewui_light.txt" : "basalt_previewui.txt");
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    /// <summary>退出时清理外部打开兜底产生的临时文件（VMDK / 容器模式）。</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        Basalt.Common.TempFileRegistry.Cleanup();
        base.OnExit(e);
    }

    /// <summary>取诊断参数后的第一个路径参数；允许 --light 等开关出现在路径前 / 后。</summary>
    private static string PathArg(string[] args, int start)
        => args.Skip(start).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "";

    /// <summary>把自检报告以 UTF-8 写入临时目录（默认编码会写出乱码）。</summary>
    private static void WriteReport(string report, string fileName)
    {
        string outPath = Path.Combine(Path.GetTempPath(), fileName);
        File.WriteAllText(outPath, report, new UTF8Encoding(false));
    }

    public void ToggleTheme()
    {
        var merged = Resources.MergedDictionaries;
        var light = merged.FirstOrDefault(d => d.Source?.OriginalString.EndsWith("Light.xaml") == true);
        var dark = merged.FirstOrDefault(d => d.Source?.OriginalString.EndsWith("Dark.xaml") == true);

        if (light == null || dark == null) return;

        IsDarkTheme = !IsDarkTheme;
        merged.Remove(dark);
        if (IsDarkTheme) merged.Insert(0, dark);
        else merged.Insert(merged.IndexOf(light) + 1, dark);
    }
}
