using System.Windows;
using MacDock.Core.Interop;

namespace MacDock.Core.Services;

/// <summary>
/// 光标服务：获取全局屏幕光标位置（物理像素）。
/// Dock 图标层是分层透明窗口，透明区域收不到 WM_MOUSEMOVE，
/// 鱼眼追踪必须用全局光标轮询（配合 PointFromScreen 换算 DIP）。
/// </summary>
public static class CursorService
{
    /// <summary>获取屏幕光标位置（物理像素坐标）。失败返回 null。</summary>
    public static Point? GetScreenPosition()
    {
        if (!NativeMethods.GetCursorPos(out var p))
            return null;

        return new Point(p.x, p.y);
    }
}
