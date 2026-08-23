namespace MacDock.Core.Models;

/// <summary>
/// 单个托盘图标（通知区按钮）的只读快照。点击转发所需的全部字段均由托盘项数据携带。
/// </summary>
/// <param name="Key">托盘项的全局唯一键（hWnd + uID），用于增删差量更新。</param>
/// <param name="HIcon">托盘图标句柄（仅用于转成 BitmapSource）。</param>
/// <param name="Tooltip">悬停提示文本；可能为空。</param>
/// <param name="IsOverflow">是否来自溢出区（通知区折叠图标）。</param>
/// <param name="HwndTarget">接收点击消息的窗口句柄。</param>
/// <param name="UCallbackMessage">点击回调消息 ID。</param>
/// <param name="UId">托盘项 ID（点击转发 wParam 用）。</param>
public sealed record TrayIconInfo(
    string Key,
    IntPtr HIcon,
    string? Tooltip,
    bool IsOverflow,
    IntPtr HwndTarget,
    uint UCallbackMessage,
    uint UId)
{
    /// <summary>根据目标窗口与 ID 生成稳定键（大小写不敏感资源无关，纯身份）。</summary>
    public static string BuildKey(IntPtr hwndTarget, uint uID)
        => $"{hwndTarget.ToInt64():X}:{uID}";
}
