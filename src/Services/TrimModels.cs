using System.IO;

namespace Basalt.Services;

/// <summary>一键精简的阶段。UI 直接把它映射成「提取中 → 清理中 → 封装中 → 替换中」等文案。</summary>
public enum TrimStage
{
    Preparing,
    Extracting,   // 从 ISO 里把 install.wim 取出（原始映像，绝不修改）
    Applying,     // apply 到临时工作目录
    Cleaning,     // 清理手术
    Capturing,    // capture / export 重新封装
    Replacing,    // 把新 WIM 替换进 ISO 副本
    Done,
    Failed,
    Cancelled
}

public static class TrimStageNames
{
    public static string Of(TrimStage stage) => stage switch
    {
        TrimStage.Preparing => "准备中",
        TrimStage.Extracting => "提取中",
        TrimStage.Applying => "释放中",
        TrimStage.Cleaning => "清理中",
        TrimStage.Capturing => "封装中",
        TrimStage.Replacing => "替换中",
        TrimStage.Done => "已完成",
        TrimStage.Failed => "已失败",
        TrimStage.Cancelled => "已取消",
        _ => stage.ToString()
    };

    public static bool IsFinal(TrimStage stage)
        => stage is TrimStage.Done or TrimStage.Failed or TrimStage.Cancelled;
}

/// <summary>精简流水线的进度快照。Percent 为 null 时进度条显示 Loading 动画。</summary>
public sealed class TrimProgress
{
    public TrimStage Stage { get; init; }
    public string Message { get; init; } = "";
    public string Detail { get; init; } = "";
    public int? Percent { get; init; }
    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }

    public string StageText => TrimStageNames.Of(Stage);
    public bool IsFinal => TrimStageNames.IsFinal(Stage);
}

/// <summary>一次精简任务的全部输入。</summary>
public sealed class TrimOptions
{
    /// <summary>源 ISO 的真实文件路径（只读，绝不写入）。</summary>
    public string SourceIsoPath { get; init; } = "";

    /// <summary>ISO 检查结果：提供 install.wim 路径、大小、版本列表。</summary>
    public IsoInspectionResult Inspection { get; init; } = new();

    /// <summary>要精简的版本索引（WIM 内的 IMAGE INDEX）。</summary>
    public int ImageIndex { get; init; } = 1;

    public string ImageName { get; init; } = "";
    public string ImageDescription { get; init; } = "Basalt 精简版";

    public bool RemoveDefender { get; init; }
    public bool RemoveEdge { get; init; }
    public bool RemoveDrivers { get; init; }

    /// <summary>是否用 export 保留其它未精简的版本（精简后的版本固定排到索引 #1）。</summary>
    public bool KeepOtherImages { get; init; } = true;

    /// <summary>工作根目录，默认 %TEMP%\Basalt_Wim_Work；空间不足时自动换到剩余空间最大的固定盘。</summary>
    public string WorkRoot { get; init; } = "";

    /// <summary>输出 ISO；为空时写到源 ISO 同目录的 *-basalt-trimmed.iso。</summary>
    public string OutputIsoPath { get; init; } = "";

    /// <summary>调试用：结束后保留工作目录（默认自动清理）。</summary>
    public bool KeepWorkDir { get; init; }

    public bool HasAnyOption => RemoveDefender || RemoveEdge || RemoveDrivers;
}

/// <summary>精简结果。</summary>
public sealed class TrimResult
{
    public bool Success { get; set; }
    public bool Cancelled { get; set; }
    public string? Error { get; set; }
    public string? OutputIsoPath { get; set; }
    public string? WorkRoot { get; set; }

    public long OriginalIsoSize { get; set; }
    public long OriginalWimSize { get; set; }
    public long NewWimSize { get; set; }
    public long OutputLogicalSize { get; set; }

    /// <summary>逻辑尺寸不变时，稀疏写入真正省下的物理字节。</summary>
    public long SavedBytes => Math.Max(0, OriginalWimSize - NewWimSize);

    public CleanupResult? Cleanup { get; set; }
    public List<string> StageLog { get; } = new();
    public List<string> Notes { get; } = new();

    public string SizeText => $"{Models.FileItemFormatter.FormatSize(OriginalWimSize)} → " +
                              $"{Models.FileItemFormatter.FormatSize(NewWimSize)}" +
                              $"（省下 {Models.FileItemFormatter.FormatSize(SavedBytes)}）";
}
