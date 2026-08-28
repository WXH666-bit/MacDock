using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MacDock.Core.Services;

namespace MacDock.UI.Controls;

/// <summary>
/// 鱼眼放大面板：鼠标悬停时，离鼠标最近的图标放大到 MaxScale，相邻图标按距离余弦衰减。
/// 动画由 CompositionTarget.Rendering 驱动增量插值（约 200ms EaseOut 视觉）。
/// 鼠标位置用 GetCursorPos 全局轮询（分层透明窗口的 alpha=0 区域收不到鼠标消息）。
/// 面板高度上报 IconSize * MaxScale：放大的图标在窗口内向上生长，不会被裁剪。
/// </summary>
public sealed class FishEyePanel : Panel
{
    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(nameof(IconSize), typeof(double), typeof(FishEyePanel),
            new FrameworkPropertyMetadata(56.0,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty MaxScaleProperty =
        DependencyProperty.Register(nameof(MaxScale), typeof(double), typeof(FishEyePanel),
            new FrameworkPropertyMetadata(1.6,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty EffectRadiusProperty =
        DependencyProperty.Register(nameof(EffectRadius), typeof(double), typeof(FishEyePanel),
            new FrameworkPropertyMetadata(130.0, FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(FishEyePanel),
            new FrameworkPropertyMetadata(8.0,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty ItemExtentProperty =
        DependencyProperty.RegisterAttached(
            "ItemExtent",
            typeof(double),
            typeof(FishEyePanel),
            new FrameworkPropertyMetadata(
                double.NaN,
                FrameworkPropertyMetadataOptions.AffectsParentMeasure
                | FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static readonly DependencyProperty GroupBreakIndexProperty =
        DependencyProperty.Register(
            nameof(GroupBreakIndex),
            typeof(int),
            typeof(FishEyePanel),
            new FrameworkPropertyMetadata(
                -1,
                FrameworkPropertyMetadataOptions.AffectsMeasure
                | FrameworkPropertyMetadataOptions.AffectsArrange
                | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>基础图标尺寸。</summary>
    public double IconSize { get => (double)GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }

    /// <summary>最大放大倍数。</summary>
    public double MaxScale { get => (double)GetValue(MaxScaleProperty); set => SetValue(MaxScaleProperty, value); }

    /// <summary>放大影响半径（像素）。</summary>
    public double EffectRadius { get => (double)GetValue(EffectRadiusProperty); set => SetValue(EffectRadiusProperty, value); }

    /// <summary>图标间距。</summary>
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    /// <summary>固定项结束位置；其后存在临时运行项时绘制系统分组线。</summary>
    public int GroupBreakIndex
    {
        get => (int)GetValue(GroupBreakIndexProperty);
        set => SetValue(GroupBreakIndexProperty, value);
    }

    /// <summary>设置单个子项的静止横向尺寸；未设置时使用 IconSize。</summary>
    public static void SetItemExtent(DependencyObject element, double value)
        => element.SetValue(ItemExtentProperty, value);

    /// <summary>读取单个子项的静止横向尺寸。</summary>
    public static double GetItemExtent(DependencyObject element)
        => (double)element.GetValue(ItemExtentProperty);

    /// <summary>
    /// 悬停项变化事件：参数为（子元素索引，该图标静止中心 X，面板坐标系）。
    /// 无悬停时索引为 -1。供外层显示 macOS 风格名称气泡。
    /// </summary>
    public event Action<int, double>? HoverChanged;

    private readonly Dictionary<UIElement, double> _currentScales = new();
    private bool _animating;
    private int _hoverIndex = -1;

    private const double GroupBreakExtent = 18.0;
    private static readonly Brush GroupBreakBrush = CreateGroupBreakBrush();

    public FishEyePanel()
    {
        Loaded += (_, _) => StartAnimation();
        Unloaded += (_, _) => StopAnimation();
    }

    /// <summary>首尾各追加的内边距，容纳边缘图标放大后的横向溢出。</summary>
    private double EdgePadding => IconSize * (MaxScale - 1.0) / 2.0 + 6.0;

    /// <summary>静止图标行的净宽（不含 EdgePadding），供背景玻璃条按此定宽。</summary>
    public double StaticContentWidth
    {
        get
        {
            if (Children.Count == 0)
                return 0;

            var width = (Children.Count - 1) * Spacing;
            foreach (UIElement child in Children)
                width += BaseExtent(child);

            if (HasGroupBreak)
                width += GroupBreakExtent;

            return width;
        }
    }

    /// <summary>第 i 个图标的静止中心 X（面板坐标系）。</summary>
    private double CenterX(int index)
        => ItemLeft(index) + BaseExtent(Children[index]) / 2.0;

    private bool HasGroupBreak
        => GroupBreakIndex > 0 && GroupBreakIndex < Children.Count;

    private static Brush CreateGroupBreakBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0),
            EndPoint = new Point(0.5, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(105, 255, 255, 255), 0.24),
                new GradientStop(Color.FromArgb(105, 255, 255, 255), 0.76),
                new GradientStop(Color.FromArgb(0, 255, 255, 255), 1),
            },
        };
        brush.Freeze();
        return brush;
    }

    private double BaseExtent(UIElement child)
    {
        var extent = GetItemExtent(child);
        return double.IsFinite(extent) && extent > 0 ? extent : IconSize;
    }

    private bool IsMagnifiable(UIElement child)
        => BaseExtent(child) >= IconSize * 0.75;

    private double ItemLeft(int index)
    {
        var x = EdgePadding;
        for (var itemIndex = 0; itemIndex < index; itemIndex++)
        {
            x += BaseExtent(Children[itemIndex]) + Spacing;
            if (HasGroupBreak && itemIndex + 1 == GroupBreakIndex)
                x += GroupBreakExtent;
        }

        return x;
    }

    /// <summary>把面板横坐标换算为固定区中的插入边界。</summary>
    public int GetInsertionIndex(double x, int maximumIndex)
    {
        var limit = Math.Clamp(maximumIndex, 0, Children.Count);
        for (var index = 0; index < limit; index++)
        {
            if (x < CenterX(index))
                return index;
        }

        return limit;
    }

    /// <summary>获取插入边界在面板坐标系中的静止横坐标。</summary>
    public double GetInsertionX(int insertionIndex)
    {
        if (Children.Count == 0)
            return ActualWidth > 0 ? ActualWidth / 2.0 : EdgePadding;

        var index = Math.Clamp(insertionIndex, 0, Children.Count);
        if (index == 0)
            return Math.Max(0, ItemLeft(0) - Spacing / 2.0);

        var previousRight = ItemLeft(index - 1) + BaseExtent(Children[index - 1]);
        if (index == Children.Count)
            return previousRight + Spacing / 2.0;

        return (previousRight + ItemLeft(index)) / 2.0;
    }

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
        // 按静止尺寸测量：DesiredSize 若大于 Arrange 槽位，WPF 会按测量尺寸渲染再裁剪，
        // 导致图标底部被切。放大时槽位大于测量值，Stretch 对齐会自动拉伸填满，不会裁剪。
        foreach (UIElement child in Children)
            child.Measure(new Size(BaseExtent(child), IconSize));

        double width = StaticContentWidth == 0 ? 0 : StaticContentWidth + 2 * EdgePadding;
        // 高度上报放大后的最大尺寸：放大的图标贴底向上生长，全程在窗口内不被裁剪
        return new Size(width, IconSize * MaxScale);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int count = Children.Count;
        for (int i = 0; i < count; i++)
        {
            var child = Children[i];
            var magnifiable = IsMagnifiable(child);
            double scale = magnifiable && _currentScales.TryGetValue(child, out var s)
                ? s
                : 1.0;
            double w = BaseExtent(child) * scale;
            double h = IconSize * scale;
            double x = CenterX(i) - w / 2.0;
            double y = finalSize.Height - h; // 贴底缩放：放大向上生长
            child.Arrange(new Rect(x, y, w, h));
        }

        return finalSize;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (!HasGroupBreak)
            return;

        var previousIndex = GroupBreakIndex - 1;
        var previousRight = ItemLeft(previousIndex) + BaseExtent(Children[previousIndex]);
        var nextLeft = ItemLeft(GroupBreakIndex);
        var x = (previousRight + nextLeft) / 2.0;
        var top = Math.Max(0, ActualHeight - IconSize + 9);
        var height = Math.Max(0, IconSize - 18);
        drawingContext.DrawRoundedRectangle(
            GroupBreakBrush,
            pen: null,
            new Rect(x - 0.75, top, 1.5, height),
            radiusX: 0.75,
            radiusY: 0.75);
    }

    protected override void OnVisualChildrenChanged(
        DependencyObject visualAdded,
        DependencyObject visualRemoved)
    {
        base.OnVisualChildrenChanged(visualAdded, visualRemoved);
        if (visualRemoved is UIElement removed)
            _currentScales.Remove(removed);

        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        int count = Children.Count;
        if (count == 0)
            return;

        var mouse = GetLocalCursor();
        bool changed = false;
        int hoverIndex = -1;
        double bestDx = double.MaxValue;

        for (int i = 0; i < count; i++)
        {
            var child = Children[i];
            double target = 1.0;
            var magnifiable = IsMagnifiable(child);
            if (mouse.HasValue && magnifiable)
            {
                double dx = mouse.Value.X - CenterX(i);
                double abs = Math.Abs(dx);
                double factor = abs / EffectRadius;
                double falloff = factor >= 1.0 ? 0.0 : Math.Cos(factor * Math.PI / 2.0);
                target = 1.0 + (MaxScale - 1.0) * falloff;

                if (abs <= BaseExtent(child) / 2.0 + Spacing / 2.0 && abs < bestDx)
                {
                    bestDx = abs;
                    hoverIndex = i;
                }
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

        if (hoverIndex != _hoverIndex)
        {
            _hoverIndex = hoverIndex;
            HoverChanged?.Invoke(hoverIndex, hoverIndex >= 0 ? CenterX(hoverIndex) : 0);
        }
    }

    /// <summary>
    /// 获取面板坐标系下的光标位置；不在感应区（横向面板范围、纵向面板加下缘容差）返回 null。
    /// </summary>
    private Point? GetLocalCursor()
    {
        if (PresentationSource.FromVisual(this) is null)
            return null;

        var screen = CursorService.GetScreenPosition();
        if (!screen.HasValue)
            return null;

        Point p;
        try
        {
            p = PointFromScreen(screen.Value);
        }
        catch
        {
            return null;
        }

        // 感应区：横向面板范围 ±8；纵向从面板顶到面板底 +18（覆盖玻璃条下缘到屏幕底边）
        if (p.X < -8 || p.X > ActualWidth + 8 || p.Y < -4 || p.Y > ActualHeight + 18)
            return null;

        return p;
    }
}
