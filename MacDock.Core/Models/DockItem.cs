namespace MacDock.Core.Models;

/// <summary>
/// Dock 栏上的一个项目（应用 / 快捷方式）。
/// </summary>
public sealed class DockItem
{
    /// <summary>显示名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>启动目标路径（.lnk 已解析为真实目标）。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>用于提取图标的路径（可与 Path 不同，如 .lnk 自定义图标）。</summary>
    public string? IconPath { get; set; }

    /// <summary>启动参数。</summary>
    public string? Arguments { get; set; }

    /// <summary>是否为内置默认项。</summary>
    public bool IsBuiltIn { get; set; }
}
