using MacDock.Core.Interop;

namespace MacDock.Core.Services;

/// <summary>
/// 托盘点击转发：把菜单栏托盘图标的鼠标事件以老协议 PostMessage 回传给托盘目标窗口。
///
/// 已知限制：NOTIFYICONDATA uVersion=4 的较新应用其 uCallbackMessage 参数语义不同，
/// 本服务按 v3 及以下的老协议发送（绝大多数托盘应用兼容）；个别应用若行为异常，记录日志即可。
/// </summary>
public static class TrayIconForwarder
{
    /// <summary>
    /// 转发一次鼠标事件。
    /// </summary>
    /// <param name="hwndTarget">托盘目标窗口句柄（TrayIconInfo.HwndTarget）。</param>
    /// <param name="uCallbackMessage">点击回调消息 ID（TrayIconInfo.UCallbackMessage）。</param>
    /// <param name="uId">托盘项 ID（TrayIconInfo 携带；此处由 UI 传入）。</param>
    /// <param name="mouseMessage">鼠标消息：左键 WM_LBUTTONUP、右键 WM_RBUTTONUP、双击 WM_LBUTTONDBLCLK。</param>
    /// <returns>是否投递成功。</returns>
    public static bool SendClick(IntPtr hwndTarget, uint uCallbackMessage, uint uId, uint mouseMessage)
    {
        if (hwndTarget == IntPtr.Zero || uCallbackMessage == 0)
            return false;

        return TrayInterop.PostMessageW(
            hwndTarget,
            uCallbackMessage,
            (IntPtr)uId,
            (IntPtr)mouseMessage);
    }

    /// <summary>左键单击消息（WM_LBUTTONUP）。</summary>
    public const uint MouseLeftButtonUp = TrayInterop.WM_LBUTTONUP;

    /// <summary>右键单击消息（WM_RBUTTONUP）。</summary>
    public const uint MouseRightButtonUp = TrayInterop.WM_RBUTTONUP;

    /// <summary>左键双击消息（WM_LBUTTONDBLCLK）。</summary>
    public const uint MouseLeftDoubleClick = TrayInterop.WM_LBUTTONDBLCLK;
}
