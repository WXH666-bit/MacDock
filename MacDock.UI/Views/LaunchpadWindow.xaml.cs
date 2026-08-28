using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using MacDock.UI.ViewModels;
using NLog;

namespace MacDock.UI.Views;

/// <summary>全屏启动台：Esc、失焦、点击空白或成功启动应用时关闭。</summary>
public partial class LaunchpadWindow : Window
{
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();
    private static readonly TimeSpan EnterDuration = TimeSpan.FromMilliseconds(190);
    private static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(130);

    private readonly LaunchpadViewModel _viewModel;
    private bool _closing;

    public LaunchpadWindow(LaunchpadViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        DataContext = _viewModel;

        _viewModel.AppLaunched += RequestClose;
        Loaded += OnLoaded;
        Deactivated += (_, _) => RequestClose();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AnimateIn();
        SearchBox.Focus();

        try
        {
            await _viewModel.LoadAsync();
        }
        catch (Exception exception)
        {
            // ViewModel 已收敛目录异常；此处兜住窗口关闭等生命周期竞态。
            Logger.Error(exception, "启动台加载任务发生未预期异常");
        }
    }

    private void AnimateIn()
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            Opacity = 1;
            LaunchpadContent.Opacity = 1;
            ContentScale.ScaleX = 1;
            ContentScale.ScaleY = 1;
            return;
        }

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, EnterDuration));
        LaunchpadContent.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, EnterDuration));
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        ContentScale.BeginAnimation(
            System.Windows.Media.ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.97, 1, EnterDuration) { EasingFunction = easing });
        ContentScale.BeginAnimation(
            System.Windows.Media.ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.97, 1, EnterDuration) { EasingFunction = easing });
    }

    private void OnBackdropClick(object sender, MouseButtonEventArgs e)
    {
        RequestClose();
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        RequestClose();
        e.Handled = true;
    }

    private void RequestClose()
    {
        if (_closing)
            return;

        _closing = true;
        if (!SystemParameters.ClientAreaAnimation || !IsVisible)
        {
            Close();
            return;
        }

        var fade = new DoubleAnimation(0, ExitDuration)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    protected override void OnClosed(EventArgs e)
    {
        Loaded -= OnLoaded;
        _viewModel.AppLaunched -= RequestClose;
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
