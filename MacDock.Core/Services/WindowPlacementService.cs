using System.Windows;

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
}
