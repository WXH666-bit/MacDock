using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MacDock.Animations;

/// <summary>
/// 点击弹跳动画：图标沿 Y 轴向上弹起后回落（BounceEase 落地弹跳感）。
/// </summary>
public static class BounceAnimation
{
    /// <summary>对目标元素播放一次弹跳（向上 distance 像素，总时长 durationMs）。</summary>
    public static void Play(FrameworkElement target, double distance = 12, double durationMs = 600)
    {
        if (target.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            target.RenderTransform = transform;
        }

        var animation = new DoubleAnimation
        {
            From = 0,
            To = -distance,
            Duration = TimeSpan.FromMilliseconds(durationMs / 2),
            AutoReverse = true,
            EasingFunction = new BounceEase { EasingMode = EasingMode.EaseOut },
        };

        transform.BeginAnimation(TranslateTransform.YProperty, animation);
    }
}
