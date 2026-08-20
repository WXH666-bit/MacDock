using MacDock.Core.Interop;

namespace MacDock.Core.Services;

/// <summary>
/// 窗口样式服务：为 Dock / 菜单栏等无边框置顶窗口设置扩展样式。
/// </summary>
public static class WindowStyleService
{
    /// <summary>
    /// 应用 Dock 窗口扩展样式：置顶、点击不抢焦点、不出现在 Alt+Tab。
    /// 必须在窗口 SourceInitialized 之后调用。
    /// </summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <param name="clickThrough">是否额外附加鼠标穿透（纯视觉背景窗使用）。</param>
    public static void ApplyDockStyles(IntPtr hwnd, bool clickThrough = false)
    {
        var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        long value = ex.ToInt64()
            | NativeMethods.WS_EX_NOACTIVATE
            | NativeMethods.WS_EX_TOOLWINDOW
            | NativeMethods.WS_EX_TOPMOST;
        if (clickThrough)
            value |= NativeMethods.WS_EX_TRANSPARENT;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(value));
    }

    /// <summary>
    /// 把 hwndBelow 插到 hwndAbove 的正下方（Z 序），用于 Dock 背景窗贴在图标层之下。
    /// </summary>
    public static void PlaceBelow(IntPtr hwndBelow, IntPtr hwndAbove)
    {
        NativeMethods.SetWindowPos(hwndBelow, hwndAbove, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }
}
