using MacDock.Core.Interop;

namespace MacDock.Core.Services;

/// <summary>
/// 窗口毛玻璃背景：Win11 22H2+ 走 DWM 亚克力；否则降级由 UI 自绘半透明背景。
/// 注意：DWM 亚克力要求窗口为非分层窗口，M1 的 Dock 采用分层透明自绘，此服务为 M2 菜单栏预留。
/// </summary>
public static class SystemBackdropService
{
    /// <summary>系统是否支持 DWM 亚克力系统背景（Win11 22H2+，build 22621+）。</summary>
    public static bool IsAcrylicSupported => Environment.OSVersion.Version.Build >= 22621;

    /// <summary>应用亚克力背景（仅 Win11 22H2+ 生效）。</summary>
    public static void ApplyAcrylic(IntPtr hwnd)
    {
        if (!IsAcrylicSupported)
            return;

        int backdrop = NativeMethods.DWMSBT_TRANSIENTWINDOW; // 3 = 亚克力
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
    }

    /// <summary>应用 DWM 圆角（仅 Win11 生效）。</summary>
    public static void ApplyRoundedCorners(IntPtr hwnd)
    {
        if (Environment.OSVersion.Version.Build < 22000)
            return;

        int preference = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }
}
