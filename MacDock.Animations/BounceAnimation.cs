using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MacDock.Animations;

/// <summary>
/// 点击弹跳动画：图标沿 Y 轴快速向上弹起（EaseOut），回落时带 BounceEase 落地弹跳感。
/// </summary>
public static class BounceAnimation
{
    /// <summary>向上段时长（毫秒）。</summary>
    private const double RiseMs = 200;

    /// <summary>对目标元素播放一次弹跳（向上 distance 像素，总时长 durationMs）。</summary>
    public static void Play(FrameworkElement target, double distance = 12, double durationMs = 600)
    {
        if (target.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            target.RenderTransform = transform;
        }

        // 两段：向上快出（无弹跳）→ 回落带弹跳
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(durationMs),
        };
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(
            -distance,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(RiseMs)))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        });
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(
            0,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(durationMs)))
        {
            EasingFunction = new BounceEase { EasingMode = EasingMode.EaseOut },
        });

        transform.BeginAnimation(TranslateTransform.YProperty, animation);
    }
}
