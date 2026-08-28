namespace MacDock.Core.Models;

/// <summary>已安装应用的来源类型。</summary>
public enum InstalledAppKind
{
    /// <summary>来自当前用户或公共开始菜单的桌面快捷方式。</summary>
    Desktop,

    /// <summary>来自当前用户 AppListEntry 的商店应用。</summary>
    Store,
}

/// <summary>
/// 启动台可展示的已安装应用。桌面应用的 <see cref="LaunchTarget" /> 是解析后的目标路径，
/// 商店应用的 <see cref="LaunchTarget" /> 是 AUMID。
/// </summary>
public sealed record InstalledApp(
    string Name,
    InstalledAppKind Kind,
    string LaunchTarget,
    string? IconPath = null,
    string? Arguments = null)
{
    /// <summary>商店应用的 AUMID；桌面应用返回 null。</summary>
    public string? Aumid => Kind == InstalledAppKind.Store ? LaunchTarget : null;
}
