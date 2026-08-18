using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MacDock.UI.Controls;

/// <summary>
/// 鱼眼放大面板：鼠标悬停时，离鼠标最近的图标放大到 MaxScale，相邻图标按距离余弦衰减。
/// 动画由 CompositionTarget.Rendering 驱动增量插值（约 200ms EaseOut 视觉）。
/// </summary>
public sealed class FishEyePanel : Panel
{
    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(FishEyePanel),
            new FrameworkPropertyMetadata(48.0,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty MaxScaleProperty =
        DependencyProperty.Register(nameof(MaxScale), typeof(double), typeof(FishEyePanel),
            new FrameworkPropertyMetadata(1.6, FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty EffectRadiusProperty =
        DependencyProperty.Register(nameof(EffectRadius), typeof(double), typeof(FishEyePanel),
            new FrameworkPropertyMetadata(120.0, FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(FishEyePanel),
            new FrameworkPropertyMetadata(12.0,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>基础图标尺寸。</summary>
    public double IconSize { get => (double)GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }

    /// <summary>最大放大倍数。</summary>
    public double MaxScale { get => (double)GetValue(MaxScaleProperty); set => SetValue(MaxScaleProperty, value); }

    /// <summary>放大影响半径（像素）。</summary>
    public double EffectRadius { get => (double)GetValue(EffectRadiusProperty); set => SetValue(EffectRadiusProperty, value); }

    /// <summary>图标间距。</summary>
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    private readonly Dictionary<UIElement, double> _currentScales = new();
    private bool _animating;

    public FishEyePanel()
    {
        Loaded += (_, _) => StartAnimation();
        Unloaded += (_, _) => StopAnimation();
    }

    private double Slot => IconSize + Spacing;

    private void StartAnimation()
    {
        if (_animating)
            return;

        _animating = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopAnimation()
    {
        if (!_animating)
            return;

        _animating = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double maxSize = IconSize * MaxScale;
        var childSize = new Size(maxSize, maxSize);
        foreach (UIElement child in Children)
            child.Measure(childSize);

        int count = Children.Count;
        double width = count == 0 ? 0 : count * Slot + Spacing;
        return new Size(width, maxSize);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int count = Children.Count;
        double slot = Slot;

        for (int i = 0; i < count; i++)
        {
            var child = Children[i];
            double scale = _currentScales.TryGetValue(child, out var s) ? s : 1.0;
            double w = IconSize * scale;
            double h = IconSize * scale;
            double centerX = Spacing + IconSize / 2.0 + i * slot;
            double x = centerX - w / 2.0;
            double y = finalSize.Height - h;
            child.Arrange(new Rect(x, y, w, h));
        }

        return finalSize;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        int count = Children.Count;
        if (count == 0)
            return;

        Point mouse;
        try
        {
            mouse = Mouse.GetPosition(this);
        }
        catch
        {
            mouse = new Point(double.NaN, double.NaN);
        }

        double slot = Slot;
        bool changed = false;

        for (int i = 0; i < count; i++)
        {
            var child = Children[i];
            double target = 1.0;
            if (!double.IsNaN(mouse.X))
            {
                double centerX = Spacing + IconSize / 2.0 + i * slot;
                double dx = mouse.X - centerX;
                double factor = Math.Abs(dx) / EffectRadius;
                double falloff = factor >= 1.0 ? 0.0 : Math.Cos(factor * Math.PI / 2.0);
                target = 1.0 + (MaxScale - 1.0) * falloff;
            }

            double current = _currentScales.TryGetValue(child, out var c) ? c : 1.0;
            double next = current + (target - current) * 0.25;
            if (Math.Abs(next - target) < 0.001)
                next = target;

            if (Math.Abs(next - current) > 0.0001)
            {
                _currentScales[child] = next;
                changed = true;
            }
        }

        if (changed)
            InvalidateArrange();
    }
}
