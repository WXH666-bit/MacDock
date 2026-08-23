using System.Windows;
using MacDock.Core.Interop;

namespace MacDock.Core.Services;

/// <summary>
/// 窗口定位：计算 Dock 底部居中位置（基于主屏工作区，DPI 已由 WPF 换算为 DIP）。
/// </summary>
public static class WindowPlacementService
{
    /// <summary>计算底部居中位置。</summary>
    public static (double Left, double Top) GetBottomCenter(double width, double height, double bottomMargin = 4)
    {
        var workArea = SystemParameters.WorkArea;
        double left = workArea.Left + (workArea.Width - width) / 2.0;
        double top = workArea.Bottom - height - bottomMargin;
        return (left, top);
    }

    /// <summary>
    /// 主屏顶部通栏定位：直接用物理像素通过 SetWindowPos 落位，绕开 WPF 的
    /// DIP → 像素取整，保证 125% / 150% 缩放下左右边缘不露缝。
    /// </summary>
    /// <param name="hwnd">目标窗口句柄。</param>
    /// <param name="heightDip">通栏高度（DIP）。</param>
    /// <param name="dpiScaleY">该窗口当前的纵向 DPI 缩放系数（如 1.5）。</param>
    /// <returns>是否落位成功。</returns>
    public static bool StretchToPrimaryTop(IntPtr hwnd, double heightDip, double dpiScaleY)
    {
        if (hwnd == IntPtr.Zero || heightDip <= 0 || dpiScaleY <= 0)
            return false;

        if (!TryGetPrimaryMonitorBounds(out var bounds))
            return false;

        int heightPx = (int)Math.Ceiling(heightDip * dpiScaleY);

        // 覆盖整个显示器宽度（含任务栏区域），而非工作区：菜单栏本身就要压在最顶端
        return NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            bounds.left,
            bounds.top,
            bounds.right - bounds.left,
            heightPx,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>取主显示器完整边界（物理像素）。</summary>
    private static bool TryGetPrimaryMonitorBounds(out RECT bounds)
    {
        bounds = default;

        var monitor = NativeMethods.MonitorFromPoint(
            new POINT { x = 0, y = 0 },
            NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        if (monitor == IntPtr.Zero)
            return false;

        var info = new NativeMethods.MONITORINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };

        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            return false;

        bounds = info.rcMonitor;
        return bounds.right > bounds.left;
    }
}
