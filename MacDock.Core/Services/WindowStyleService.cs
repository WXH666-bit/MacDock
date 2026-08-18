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
    public static void ApplyDockStyles(IntPtr hwnd)
    {
        var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        long value = ex.ToInt64()
            | NativeMethods.WS_EX_NOACTIVATE
            | NativeMethods.WS_EX_TOOLWINDOW
            | NativeMethods.WS_EX_TOPMOST;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(value));
    }
}
