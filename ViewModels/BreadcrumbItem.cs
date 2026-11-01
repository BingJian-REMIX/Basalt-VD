using Basalt.Models;

namespace Basalt.ViewModels;

/// <summary>
/// 顶部路径面包屑的一节。示例：Windows 11 x64.vmdk &gt; 分区 3 (NTFS) &gt; Users &gt; Admin
/// 每节携带它所代表的"引擎路径"，点击即可跳转到该层级。
/// </summary>
public sealed class BreadcrumbItem
{
    public required string Display { get; init; }

    /// <summary>该层级对应的引擎路径（VHD: 真实路径；VMDK: "0\DIR\SUB" 伪路径）。</summary>
    public required string Path { get; init; }

    /// <summary>是否是最深一级（当前所在位置，样式上不加粗、不可点击）。</summary>
    public bool IsCurrent { get; init; }

    /// <summary>是否是最后一级（决定是否显示尾部分隔符 "›"）。</summary>
    public bool IsLast { get; init; }

    public override string ToString() => Display;
}
