using System.Diagnostics;
using System.IO;
using System.Windows;
using Basalt.Models;
using Basalt.Services;
using Microsoft.Win32;

namespace Basalt;

/// <summary>
/// ISO 镜像检查器：展示 sources / install.wim(esd) 信息、Windows 版本列表、文件清单，
/// 并驱动「一键精简」流水线（提取 → 释放 → 清理 → 封装 → 替换）。
/// <para>
/// 检查器默认自建独立的 ContainerDiskService（ownsDisk = true），不持有主窗口那个随时可能
/// 被卸载 / 切栈的 _disk；非模态显示时也不会出现悬空引用。
/// </para>
/// </summary>
public partial class IsoInspectorWindow : Window
{
    private readonly IVirtualDisk _disk;
    private readonly bool _ownsDisk;
    private readonly string? _sourceIsoPath;
    private bool _loading;
    private bool _trimRunning;
    private CancellationTokenSource? _cts;

    /// <summary>最近一次检查结果（渲染自测可读）。</summary>
    public IsoInspectionResult? Result { get; private set; }

    /// <summary>源 ISO 的真实路径；嵌套容器 / 非 ISO 引擎时为 null。</summary>
    public string? SourceIsoPath => _sourceIsoPath;

    /// <summary>当前选中的 Windows 版本（自测可读）。</summary>
    public WindowsImageInfo? SelectedImage => VersionList.SelectedItem as WindowsImageInfo;

    public string WorkRoot => WorkDirBox.Text.Trim();

    public IsoInspectorWindow(IVirtualDisk disk, string displayName, bool ownsDisk = false)
    {
        _disk = disk;
        _ownsDisk = ownsDisk;
        _sourceIsoPath = disk is ContainerDiskService { Format: ContainerFormat.Iso } c
            ? c.ArchivePath
            : null;
        InitializeComponent();

        Title = $"ISO 镜像检查器 — {displayName}";
        IsoNameText.Text = displayName;
        WorkDirBox.Text = TrimWorkflow.DefaultWorkRoot();
        EngineText.Text = "引擎：" + WimTool.Resolve().Label;
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
        ApplyTrimButton.IsEnabled = false;
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
            UpdateTrimSummary();   // 读取结束后按最终状态恢复「开始精简」按钮
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
            : "点击某一行选择要精简的版本。";
        if (result.Images.Count > 0) VersionList.SelectedIndex = 0;

        FileList.ItemsSource = result.Files;
        ExtractButton.IsEnabled = result.InstallImagePath != null;
        ExtractButton.Content = result.InstallImageName == null
            ? "提取安装映像到本地"
            : $"提取 {result.InstallImageName} 到本地";

        HintText.Text = result.Error ?? "读取完成。";
        UpdateTrimSummary();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private void VersionList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => UpdateTrimSummary();

    private void TrimOption_Changed(object sender, RoutedEventArgs e) => UpdateTrimSummary();

    /// <summary>把当前 UI 选择转成一次精简任务的输入（自测可直接复用）。</summary>
    public TrimOptions CreateTrimOptions()
    {
        var image = SelectedImage;
        return new TrimOptions
        {
            SourceIsoPath = _sourceIsoPath ?? "",
            Inspection = Result ?? new IsoInspectionResult(),
            ImageIndex = image?.Index ?? 1,
            ImageName = image?.Name ?? "Basalt 精简版",
            ImageDescription = string.IsNullOrWhiteSpace(image?.Description)
                ? $"Basalt 精简版（基于 {image?.Name}）"
                : image!.Description,
            RemoveDefender = DefenderBox.IsChecked == true,
            RemoveEdge = EdgeBox.IsChecked == true,
            RemoveDrivers = DriverBox.IsChecked == true,
            KeepOtherImages = KeepOthersBox.IsChecked == true,
            WorkRoot = WorkRoot
        };
    }

    /// <summary>勾选框只更新说明文字与按钮可用性，真正的镜像修改只在点击「开始精简」后发生。</summary>
    private void UpdateTrimSummary()
    {
        var selected = new List<string>();
        if (DefenderBox?.IsChecked == true) selected.Add("Defender");
        if (EdgeBox?.IsChecked == true) selected.Add("Edge/Xbox");
        if (DriverBox?.IsChecked == true) selected.Add("冗余驱动");

        string imageText = SelectedImage is { } img ? $"#{img.Index} {img.Name}" : "未选择版本";
        string head = selected.Count == 0
            ? "未选择任何精简项。"
            : $"已选 {selected.Count} 项：{string.Join("、", selected)}。";

        string estimate = "";
        try
        {
            if (_sourceIsoPath != null && File.Exists(_sourceIsoPath) && Result != null)
            {
                long need = TrimWorkflow.EstimateRequiredBytes(CreateTrimOptions(),
                    new FileInfo(_sourceIsoPath).Length);
                estimate = $" 预计需 {FileItemFormatter.FormatSize(need)} 临时空间。";
            }
        }
        catch { /* 预估失败不影响可用性 */ }

        string reason = "";
        bool canTrim = true;
        if (_sourceIsoPath == null)
        {
            canTrim = false;
            reason = " 当前引擎不是本地 ISO 文件，无法做原位替换。";
        }
        else if (Result?.InstallImagePath == null)
        {
            canTrim = false;
            reason = " 未找到安装映像。";
        }
        else if (SelectedImage == null)
        {
            canTrim = false;
            reason = " 请先选择一个 Windows 版本。";
        }
        else if (!WimTool.Resolve().Available)
        {
            canTrim = false;
            reason = " 缺少 wimlib / DISM 引擎。";
        }
        else if (selected.Count == 0)
        {
            canTrim = false;
            reason = " 请先勾选至少一个精简项。";
        }

        if (TrimSummary != null)
            TrimSummary.Text = $"{imageText} · {head}{estimate}{reason}";
        if (ApplyTrimButton != null)
            ApplyTrimButton.IsEnabled = canTrim && !_trimRunning && !_loading;
    }

    private void BrowseWorkDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择精简工作目录（需要较大的可用空间）",
            InitialDirectory = Directory.Exists(WorkRoot) ? WorkRoot : Path.GetTempPath()
        };
        if (dialog.ShowDialog(this) == true)
        {
            WorkDirBox.Text = dialog.FolderName;
            UpdateTrimSummary();
        }
    }

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

    private async void Trim_Click(object sender, RoutedEventArgs e)
    {
        if (_trimRunning || _sourceIsoPath == null || Result?.InstallImagePath == null) return;
        var options = CreateTrimOptions();
        if (!options.HasAnyOption || SelectedImage == null) return;

        string output = TrimWorkflow.ResolveOutputPath("", _sourceIsoPath);
        long need = TrimWorkflow.EstimateRequiredBytes(options, new FileInfo(_sourceIsoPath).Length);
        string confirm =
            $"即将精简 #{options.ImageIndex} {options.ImageName}\n\n" +
            $"· 精简项：{(options.RemoveDefender ? "Defender " : "")}" +
            $"{(options.RemoveEdge ? "Edge/Xbox " : "")}" +
            $"{(options.RemoveDrivers ? "冗余驱动" : "")}\n" +
            $"· 保留其它版本：{(options.KeepOtherImages ? "是" : "否（输出仅含精简版）")}\n" +
            $"· 工作目录：{options.WorkRoot}\n" +
            $"· 预计临时空间：{FileItemFormatter.FormatSize(need)}\n" +
            $"· 输出：{output}\n\n" +
            "源 ISO 全程只读，只会生成新的 ISO 副本。整个过程可能需要较长时间，确定继续？";
        if (MessageBox.Show(this, confirm, "Basalt — 确认精简",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _trimRunning = true;
        ApplyTrimButton.IsEnabled = false;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        var win = new ProgressWindow { Owner = this };
        win.SetupTrim($"精简 #{options.ImageIndex} {options.ImageName}");
        win.Cancelled += () =>
        {
            try { _cts?.Cancel(); } catch { /* 已释放 */ }
        };
        win.Show();

        var progress = new Progress<TrimProgress>(p => win.UpdateTrim(p));
        HintText.Text = "正在执行精简流水线...";

        TrimResult trimResult;
        try
        {
            trimResult = await Task.Run(() => TrimWorkflow.Run(options, progress, token), token);
        }
        catch (OperationCanceledException)
        {
            trimResult = new TrimResult { Cancelled = true, Error = "用户取消了操作。" };
            win.UpdateTrim(new TrimProgress { Stage = TrimStage.Cancelled, Message = "已取消" });
        }

        _trimRunning = false;
        _cts.Dispose();
        _cts = null;

        if (trimResult.Success && trimResult.OutputIsoPath != null)
        {
            win.Close();
            HintText.Text = $"精简版 ISO 已生成：{trimResult.OutputIsoPath}";
            string notes = trimResult.Notes.Count > 0
                ? "\n\n" + string.Join("\n", trimResult.Notes.Take(4))
                : "";
            MessageBox.Show(this,
                $"精简版 ISO 已生成\n\n{trimResult.OutputIsoPath}\n\n" +
                $"install.wim：{trimResult.SizeText}\n" +
                (trimResult.Cleanup?.Summary ?? "") + notes,
                "Basalt — 精简完成", MessageBoxButton.OK, MessageBoxImage.Information);
            RevealInExplorer(trimResult.OutputIsoPath);
        }
        else
        {
            HintText.Text = trimResult.Cancelled
                ? "精简已取消。"
                : "精简失败：" + trimResult.Error;
            ApplyTrimButton.IsEnabled = true;
        }
        UpdateTrimSummary();
    }

    /// <summary>完成后自动定位到输出目录（只在交互式会话里执行）。</summary>
    private static void RevealInExplorer(string path)
    {
        if (App.Headless) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true
            });
        }
        catch { /* 资源管理器不可用时忽略 */ }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_trimRunning)
        {
            try { _cts?.Cancel(); } catch { }
        }
        _cts?.Dispose();
        _cts = null;
        if (_ownsDisk)
        {
            try { _disk.Dispose(); } catch { /* 7z 是外部进程，无资源可释放 */ }
        }
        base.OnClosed(e);
    }
}
