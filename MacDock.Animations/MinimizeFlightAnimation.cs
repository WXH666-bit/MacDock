using System.Windows;

namespace MacDock.Animations;

/// <summary>某一帧的动画窗口边界与透明度。</summary>
public readonly record struct MinimizeFlightFrame(Rect Bounds, double Opacity);

/// <summary>窗口快照飞向 Dock 图标的纯数学动画轨迹。</summary>
public static class MinimizeFlightAnimation
{
    /// <summary>默认动画时长；短于系统常规操作延迟，不阻塞目标窗口。</summary>
    public static TimeSpan DefaultDuration { get; } = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// 计算三次贝塞尔轨迹上的一帧。进度会钳制到 0..1，位置、尺寸和透明度
    /// 共用 EaseInOut，确保端点精确落在源窗口与 Dock 图标上。
    /// </summary>
    public static MinimizeFlightFrame Evaluate(
        Rect source,
        Rect target,
        double progress)
    {
        Validate(source, nameof(source));
        Validate(target, nameof(target));

        var eased = Easing.EaseInOutCubic(progress);
        var start = new Point(
            source.Left + source.Width / 2.0,
            source.Top + source.Height / 2.0);
        var end = new Point(
            target.Left + target.Width / 2.0,
            target.Top + target.Height / 2.0);

        var deltaX = end.X - start.X;
        var deltaY = end.Y - start.Y;
        var lift = Math.Max(48.0, Math.Abs(deltaY) * 0.24);
        var control1 = new Point(
            start.X + deltaX * 0.12,
            start.Y + deltaY * 0.18);
        var control2 = new Point(
            end.X - deltaX * 0.16,
            end.Y - lift);
        var center = CubicBezier(start, control1, control2, end, eased);

        var width = Lerp(source.Width, target.Width, eased);
        var height = Lerp(source.Height, target.Height, eased);
        var bounds = new Rect(
            center.X - width / 2.0,
            center.Y - height / 2.0,
            width,
            height);

        // 前半段保留辨识度，接近图标时快速淡出。
        var opacity = Math.Clamp(1.0 - Math.Pow(eased, 2.2), 0.0, 1.0);
        return new MinimizeFlightFrame(bounds, opacity);
    }

    private static Point CubicBezier(
        Point p0,
        Point p1,
        Point p2,
        Point p3,
        double t)
    {
        var oneMinusT = 1.0 - t;
        var a = oneMinusT * oneMinusT * oneMinusT;
        var b = 3.0 * oneMinusT * oneMinusT * t;
        var c = 3.0 * oneMinusT * t * t;
        var d = t * t * t;
        return new Point(
            a * p0.X + b * p1.X + c * p2.X + d * p3.X,
            a * p0.Y + b * p1.Y + c * p2.Y + d * p3.Y);
    }

    private static double Lerp(double from, double to, double amount)
        => from + (to - from) * amount;

    private static void Validate(Rect rect, string parameterName)
    {
        if (rect.IsEmpty
            || rect.Width <= 0
            || rect.Height <= 0
            || !double.IsFinite(rect.Left)
            || !double.IsFinite(rect.Top)
            || !double.IsFinite(rect.Width)
            || !double.IsFinite(rect.Height))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
