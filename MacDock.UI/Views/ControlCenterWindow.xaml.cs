using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using MacDock.UI.ViewModels;

namespace MacDock.UI.Views;

/// <summary>菜单栏控制中心弹窗；可交互、失焦收起并尊重系统动画偏好。</summary>
public partial class ControlCenterWindow : Window
{
    private static readonly TimeSpan AnimationDuration = TimeSpan.FromMilliseconds(160);

    private readonly ControlCenterViewModel _viewModel;
    private bool _isOpen;

    public ControlCenterWindow(ControlCenterViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _viewModel;

        Deactivated += (_, _) => Collapse();
        BrightnessSlider.PreviewMouseUp += (_, _) => _viewModel.FlushBrightnessWrite();
    }

    public bool IsOpen => _isOpen;

    /// <summary>在菜单栏锚点下方显示，并把窗口限制在主屏工作区内。</summary>
    public void ShowBelowAnchor(Point anchorPx)
    {
        if (!_isOpen)
        {
            Show();
            _isOpen = true;
        }

        UpdateLayout();
        var source = PresentationSource.FromVisual(this);
        var transform = source?.CompositionTarget?.TransformFromDevice
            ?? System.Windows.Media.Matrix.Identity;
        var anchor = transform.Transform(anchorPx);
        var workArea = SystemParameters.WorkArea;

        Left = Math.Clamp(
            anchor.X - ActualWidth + 24,
            workArea.Left + 8,
            Math.Max(workArea.Left + 8, workArea.Right - ActualWidth - 8));
        Top = Math.Clamp(
            anchor.Y + 5,
            workArea.Top + 8,
            Math.Max(workArea.Top + 8, workArea.Bottom - ActualHeight - 8));

        if (SystemParameters.ClientAreaAnimation)
        {
            ControlCenterRoot.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0, 1, AnimationDuration));
            ControlCenterTransform.BeginAnimation(
                System.Windows.Media.TranslateTransform.YProperty,
                new DoubleAnimation(8, 0, AnimationDuration)
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
                });
        }
        else
        {
            ControlCenterRoot.Opacity = 1;
            ControlCenterTransform.Y = 0;
        }

        Activate();
    }

    public void Collapse()
    {
        if (!_isOpen)
            return;

        _isOpen = false;
        Hide();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        Collapse();
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
