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

    /// <summary>
    /// 内置图标资源（pack URI，如 pack://application:,,,/Assets/Icons/finder.png）。
    /// 设置后优先于 IconPath，仅 UI 层解析；Core 不感知。
    /// </summary>
    public string? IconOverride { get; set; }

    /// <summary>
    /// 商店应用的可执行名（如 notepad）。Path 为空时经 StoreAppResolver
    /// 解析 AUMID 启动，用于 Win11 商店化系统组件（无本地 exe 的场景）。
    /// </summary>
    public string? StoreAppName { get; set; }

    /// <summary>启动参数。</summary>
    public string? Arguments { get; set; }

    /// <summary>是否为内置默认项。</summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>是否有可见顶层窗口在运行（由 WindowMonitor 更新，运行态不持久化）。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsRunning { get; set; }
}
