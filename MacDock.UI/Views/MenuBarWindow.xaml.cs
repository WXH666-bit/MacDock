using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MacDock.Core.Services;
using MacDock.UI.ViewModels;
using NLog;

namespace MacDock.UI.Views;

/// <summary>
/// 顶部菜单栏窗口：主屏通栏 32px、无边框、置顶、点击不抢焦点、不进 Alt+Tab。
/// 背景走 Accent 亚克力（Win11 22H2+），更低版本降级为半透明自绘。
///
/// TODO(M2.2)：当前不注册 AppBar，最大化窗口的顶部 32px 会被菜单栏覆盖，
/// 标题栏右上角按钮被遮挡。是否改用 SHAppBarMessage 让系统保留工作区，
/// 待 M2.2 真机体验后决策。
/// </summary>
public partial class MenuBarWindow : Window
{
    /// <summary>通栏高度（DIP）。</summary>
    public const double BarHeight = 32;

    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

    private readonly MenuBarViewModel _viewModel;
    private AboutWindow? _aboutWindow;

    public MenuBarWindow(MenuBarViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();
        DataContext = _viewModel;
        Height = BarHeight;

        // Win11 1803 以下无 Accent 亚克力：降级为分层窗口 + 半透明自绘底色。
        // AllowsTransparency 必须在句柄创建前设置（与 DockBackdropWindow 同一做法）。
        if (!SystemBackdropService.IsAccentAcrylicSupported)
        {
            AllowsTransparency = true;
            GlassLayer.Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x20, 0x20, 0x24));
        }

        Loaded += (_, _) => PositionBar();

        // 分辨率/缩放变化后重新贴顶（多显示器扩展点：M2.1 只做主屏）
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;

        // 置顶 + 不抢焦点 + 不进 Alt+Tab，与 Dock 完全一致的一套扩展样式
        WindowStyleService.ApplyDockStyles(hwnd);

        if (SystemBackdropService.IsAccentAcrylicSupported)
        {
            // 与 Dock 玻璃条协调的半透明深灰
            SystemBackdropService.ApplyAccentAcrylic(hwnd, 0x66202024);
        }

        PositionBar();
    }

    /// <summary>贴顶通栏定位：物理像素落位，规避 125%/150% 缩放下的边缘缝隙。</summary>
    private void PositionBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        // PerMonitorV2：用该窗口自身的复合变换取实际缩放，而非全局 DPI
        var scaleY = PresentationSource.FromVisual(this)?
            .CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        if (!WindowPlacementService.StretchToPrimaryTop(hwnd, BarHeight, scaleY))
            Logger.Warn("菜单栏贴顶定位失败，退回 WPF 逻辑坐标");
    }

    /// <summary>分辨率/缩放变化后重新贴顶（回调可能来自非 UI 线程）。</summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(PositionBar);
            return;
        }

        PositionBar();
    }

    /// <summary>Logo 点击：弹出「关于本机」（单例，重复点击激活已有窗口）。</summary>
    private void OnLogoClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (_aboutWindow is not null)
            {
                _aboutWindow.Activate();
                return;
            }

            _aboutWindow = new AboutWindow();
            _aboutWindow.Closed += (_, _) => _aboutWindow = null;
            _aboutWindow.Show();
            _aboutWindow.Activate();
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "打开「关于本机」失败");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _aboutWindow?.Close();
        _aboutWindow = null;
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
