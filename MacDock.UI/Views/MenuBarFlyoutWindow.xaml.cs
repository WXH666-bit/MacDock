using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MacDock.Core.Services;
using MacDock.UI.ViewModels;
using NLog;

namespace MacDock.UI.Views;

/// <summary>
/// 音量/亮度共用浮窗：单例复用，从菜单栏图标正下方弹出。
/// 150ms 上移淡入（TranslateY 8→0 + Opacity 0→1），收起反向；
/// Deactivated 时收起（点外部自动关），重复点击由调用方 toggle。
/// </summary>
public partial class MenuBarFlyoutWindow : Window
{
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>动画时长（与 Dock 弹窗同观感）。</summary>
    private static readonly TimeSpan AnimationDuration = TimeSpan.FromMilliseconds(150);

    private readonly MenuBarFlyoutViewModel _viewModel;
    private bool _closing;
    private bool _isOpen;

    /// <summary>浮窗当前是否处于可见状态（供菜单栏做 toggle 判断）。</summary>
    public bool IsOpen => _isOpen;

    /// <summary>浮窗视图模型（供菜单栏切换内容/回灌系统值）。</summary>
    public MenuBarFlyoutViewModel ViewModel => _viewModel;

    public MenuBarFlyoutWindow(MenuBarFlyoutViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();
        DataContext = _viewModel;

        Loaded += OnLoaded;
        Deactivated += OnDeactivated;

        // 拖动滑条会短暂失去焦点（移动焦点到滑条），Deactivated 只在焦点真正离开本窗口时触发
        ValueSlider.PreviewMouseDown += (_, _) => _viewModel.BeginUserInput();
        ValueSlider.PreviewMouseUp += (_, _) => _viewModel.EndUserInput();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        AnimateIn();
    }

    /// <summary>从图标下方弹出（anchorPx 为屏幕物理像素，x=图标中线）。</summary>
    public void ShowBelowIcon(System.Windows.Point anchorPx)
    {
        try
        {
            _closing = false;
            if (!_isOpen)
            {
                Show();
                _isOpen = true;
            }

            PositionNear(anchorPx);
        }
        catch (Exception exception)
        {
            Logger.Warn(exception, "显示浮窗失败");
            try
            {
                Close();
            }
            catch
            {
            }
        }
    }

    /// <summary>收起（反向动画后隐藏，不销毁，供再次弹出复用）。</summary>
    public void Collapse()
    {
        if (_closing)
            return;

        _closing = true;
        var animation = new DoubleAnimation
        {
            To = 0,
            Duration = AnimationDuration,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        animation.Completed += (_, _) =>
        {
            _isOpen = false;
            Hide();
        };
        FlyoutRoot.BeginAnimation(OpacityProperty, animation);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // 点外部/切换窗口即收起（浮窗保持可激活，焦点离开即关）
        Collapse();
    }

    private void AnimateIn()
    {
        FlyoutRoot.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, AnimationDuration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
        FlyoutTransform.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, AnimationDuration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
    }

    /// <summary>用物理像素 SetWindowPos 落位，规避 DIP/DPI 取整露缝。</summary>
    private void PositionNear(System.Windows.Point anchorPx)
    {
        UpdateLayout();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        var scale = PresentationSource.FromVisual(this)?
            .CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        int widthPx = (int)Math.Ceiling(ActualWidth * scale);
        int heightPx = (int)Math.Ceiling(ActualHeight * scale);
        int leftPx = (int)Math.Round(anchorPx.X - widthPx / 2.0);
        int topPx = (int)Math.Round(anchorPx.Y + 4 * scale);

        WindowPlacementService.PlaceTopNoActivate(hwnd, leftPx, topPx, widthPx, heightPx);
    }
}
