using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using MacDock.Core.Services;
using MacDock.UI.ViewModels;
using NLog;

namespace MacDock.UI.Views;

/// <summary>
/// 「关于本机」窗口：普通可激活窗口（不加 WS_EX_NOACTIVATE），
/// 深色圆角卡片，机器信息在后台线程读取后回填。
/// </summary>
public partial class AboutWindow : Window
{
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();
    private readonly AboutViewModel _viewModel;

    public AboutWindow() : this(new AboutViewModel())
    {
    }

    public AboutWindow(AboutViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();
        DataContext = _viewModel;

        // 窗口先显示，数据后填：读注册表的开销不阻塞弹出
        Loaded += OnLoaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // AllowsTransparency 下 DWM 圆角不生效，圆角由 Border 自绘；此处只补系统圆角作为增强
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            SystemBackdropService.ApplyRoundedCorners(hwnd);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            await _viewModel.LoadAsync();
        }
        catch (Exception exception)
        {
            Logger.Warn(exception, "读取「关于本机」信息失败");
        }
    }

    /// <summary>无边框窗口的拖动支持。</summary>
    private void OnDragArea(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
