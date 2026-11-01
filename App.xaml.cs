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
        // 诊断模式：Basalt.exe --diag <vhd路径>，逐步执行 P/Invoke 并输出结果
        var args = e.Args;
        Headless = args.Any(a => a is "--uitest" or "--rendertest" or "--featuretest" or "--mounttest" or "--diag" or "--selftest");
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
        base.OnStartup(e);
    }

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
