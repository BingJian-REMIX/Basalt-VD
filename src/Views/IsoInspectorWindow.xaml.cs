using System.IO;
using System.Windows;
using Basalt.Models;
using Basalt.Services;
using Microsoft.Win32;

namespace Basalt;

/// <summary>
/// ISO 镜像检查器：展示 sources / install.wim(esd) 信息、Windows 版本列表、文件清单与精简选项占位。
/// <para>
/// 检查器默认自建独立的 ContainerDiskService（ownsDisk = true），不持有主窗口那个随时可能
/// 被卸载 / 切栈的 _disk；非模态显示时也不会出现悬空引用。
/// </para>
/// </summary>
public partial class IsoInspectorWindow : Window
{
    private readonly IVirtualDisk _disk;
    private readonly bool _ownsDisk;
    private bool _loading;

    /// <summary>最近一次检查结果（渲染自测可读）。</summary>
    public IsoInspectionResult? Result { get; private set; }

    public IsoInspectorWindow(IVirtualDisk disk, string displayName, bool ownsDisk = false)
    {
        _disk = disk;
        _ownsDisk = ownsDisk;
        InitializeComponent();

        Title = $"ISO 镜像检查器 — {displayName}";
        IsoNameText.Text = displayName;
        UpdateTrimSummary();
        _ = LoadAsync();
    }

    /// <summary>从真实 ISO 路径拉起检查器；窗口关闭时自行释放引擎。</summary>
    public static void Launch(string isoPath)
    {
        if (App.Headless) return;
        var disk = new ContainerDiskService(isoPath);
        var win = new IsoInspectorWindow(disk, Path.GetFileName(isoPath), ownsDisk: true)
        {
            Owner = Application.Current.MainWindow
        };
        win.Show();
    }

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        HintText.Text = "正在定位 sources 目录，并按偏移读取安装映像尾部的版本元数据...";
        ExtractButton.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => IsoInspectorService.Inspect(_disk));
            Apply(result);
        }
        catch (Exception ex)
        {
            HintText.Text = "读取失败：" + ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private void Apply(IsoInspectionResult result)
    {
        Result = result;
        SourcesText.Text = result.SourcesDisplay;
        InstallText.Text = result.InstallImageDisplay;
        SizeText.Text = result.InstallImageSizeText;
        MetaText.Text = result.Wim?.Summary ?? "未读取（缺少 install.wim / install.esd）";

        VersionList.ItemsSource = result.Images;
        VersionHint.Text = result.Images.Count == 0
            ? "未获取到版本列表 — 已降级展示 sources 目录文件清单，可直接提取安装映像到本地。"
            : "";
        VersionHint.Visibility = result.Images.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        FileList.ItemsSource = result.Files;
        ExtractButton.IsEnabled = result.InstallImagePath != null;
        ExtractButton.Content = result.InstallImageName == null
            ? "提取安装映像到本地"
            : $"提取 {result.InstallImageName} 到本地";

        HintText.Text = result.Error ?? "读取完成。";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    /// <summary>把 install.wim / install.esd 提取到本地。大文件放后台线程，避免窗口假死。</summary>
    private async void Extract_Click(object sender, RoutedEventArgs e)
    {
        var result = Result;
        if (result?.InstallImagePath == null || result.InstallImageName == null) return;

        var dialog = new SaveFileDialog
        {
            Title = "提取安装映像到本地",
            FileName = result.InstallImageName,
            Filter = "Windows 映像 (*.wim;*.esd)|*.wim;*.esd|所有文件 (*.*)|*.*"
        };
        if (dialog.ShowDialog() != true) return;

        ExtractButton.IsEnabled = false;
        HintText.Text = $"正在提取 {result.InstallImageName}（{result.InstallImageSizeText}），请稍候...";
        try
        {
            string source = result.InstallImagePath;
            string dest = dialog.FileName;
            await Task.Run(() => _disk.ExtractFile(source, dest));
            HintText.Text = File.Exists(dest)
                ? $"已提取到 {dest}（{FileItemFormatter.FormatSize(new FileInfo(dest).Length)}）"
                : "提取结束，但未找到输出文件。";
        }
        catch (Exception ex)
        {
            HintText.Text = "提取失败：" + ex.Message;
        }
        finally
        {
            ExtractButton.IsEnabled = true;
        }
    }

    private void TrimOption_Changed(object sender, RoutedEventArgs e) => UpdateTrimSummary();

    /// <summary>精简选项只更新说明文字，不触发任何镜像修改。</summary>
    private void UpdateTrimSummary()
    {
        var selected = new List<string>();
        if (DefenderBox?.IsChecked == true) selected.Add("移除 Windows Defender");
        if (EdgeBox?.IsChecked == true) selected.Add("移除 Edge");
        if (DriverBox?.IsChecked == true) selected.Add("移除冗余驱动");

        TrimSummary.Text = selected.Count == 0
            ? "未选择任何精简项。勾选仅记录 UI 状态，本版本不会修改镜像内容。"
            : $"已选 {selected.Count} 项：{string.Join("、", selected)}。仅记录 UI 状态，本版本不会修改镜像内容。";
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_ownsDisk)
        {
            try { _disk.Dispose(); } catch { /* 7z 是外部进程，无资源可释放 */ }
        }
        base.OnClosed(e);
    }
}
