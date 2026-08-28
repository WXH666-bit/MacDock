namespace MacDock.Core.Models;

/// <summary>
/// Dock 项目的类型。
/// </summary>
public enum DockItemKind
{
    /// <summary>可启动的应用或快捷方式。数值 0 保持旧 JSON 的默认兼容行为。</summary>
    Application = 0,

    /// <summary>仅用于视觉分组的分隔线。</summary>
    Separator = 1,
}
