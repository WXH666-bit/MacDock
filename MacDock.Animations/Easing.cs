namespace MacDock.Animations;

/// <summary>
/// 缓动函数集合：供 M1 鱼眼动画与 M4 飞行动画复用。
/// </summary>
public static class Easing
{
    /// <summary>EaseOut 二次缓动。</summary>
    public static double EaseOutQuad(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return 1.0 - (1.0 - t) * (1.0 - t);
    }

    /// <summary>EaseInOut 三次缓动。</summary>
    public static double EaseInOutCubic(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return t < 0.5
            ? 4.0 * t * t * t
            : 1.0 - Math.Pow(-2.0 * t + 2.0, 3.0) / 2.0;
    }
}
