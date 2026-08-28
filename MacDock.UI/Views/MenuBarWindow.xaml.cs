using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using MacDock.Core.Services;
using MacDock.Core.Services.Taskbar;
using MacDock.UI.Services;
using MacDock.UI.ViewModels;
using NLog;

namespace MacDock.UI.Views;

/// <summary>
/// 顶部菜单栏窗口：主屏通栏 32px、无边框、置顶、点击不抢焦点、不进 Alt+Tab。
/// 背景走 Accent 亚克力（Win11 22H2+），更低版本降级为半透明自绘。
///
/// M2.2 起注册 AppBar 让系统保留顶部工作区（最大化窗口不再被压）；
/// 注册失败或设置关闭时自动降级回覆盖式（M2.1 行为）。
/// </summary>
public partial class MenuBarWindow : Window
{
    /// <summary>通栏高度（DIP）。</summary>
    public const double BarHeight = 32;

    /// <summary>菜单栏关闭后的有界亮度写收尾任务。</summary>
    internal Task ShutdownCompletion => _viewModel.BrightnessShutdownCompletion;

    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

    private readonly MenuBarViewModel _viewModel;
    private readonly ThemeManager _themeManager;
    private readonly AppBarService _appBar = new();
    private readonly bool _reserveWorkArea;
    private readonly TrayIconReader _trayReader;
    private readonly TrayAreaViewModel _trayVm;
    private MenuBarFlyoutWindow? _flyout;
    private AboutWindow? _aboutWindow;
    private ControlCenterWindow? _controlCenter;
    private LaunchpadWindow? _launchpadWindow;
    private HwndSource? _hwndSource;
    private bool _hiddenForFullscreen;
    private bool _flyoutIsVolume;

    /// <param name="viewModel">菜单栏视图模型。</param>
    /// <param name="reserveWorkArea">是否注册 AppBar 保留工作区（设置项 MenuBarReserveWorkArea）。</param>
    /// <param name="trayTakeover">是否接管任务栏托盘（设置项 TrayTakeover）。</param>
    public MenuBarWindow(
        MenuBarViewModel viewModel,
        ThemeManager themeManager,
        bool reserveWorkArea = false,
        bool trayTakeover = false)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _themeManager = themeManager ?? throw new ArgumentNullException(nameof(themeManager));
        _reserveWorkArea = reserveWorkArea;

        InitializeComponent();
        DataContext = _viewModel;
        Height = BarHeight;

        // 托盘读取器 + 托盘区 VM（接管任务栏托盘；TrayTakeover=false 只显示空区）
        _trayReader = new TrayIconReader();
        _trayVm = new TrayAreaViewModel(_trayReader, trayTakeover);
        TrayRegion.DataContext = _trayVm;

        // 浮窗可被 Alt+F4 等外部路径关闭；统一工厂保证下次点击能安全重建。
        _flyout = CreateFlyout();

        // 外部音量/亮度变化（Fn 键等）刷新时同步到已打开的浮窗
        _viewModel.ControlsRefreshed += OnControlsRefreshed;

        // Win11 1803 以下无 Accent 亚克力：降级为分层窗口 + 同色系垂直渐变自绘底色
        // （纯色在 32px 通栏上显得死板，渐变与亚克尔的上下明暗方向一致）。
        // AllowsTransparency 必须在句柄创建前设置（与 DockBackdropWindow 同一做法）。
        if (!SystemBackdropService.IsAccentAcrylicSupported)
        {
            AllowsTransparency = true;
            ApplyFallbackTheme();
        }

        // Loaded 后再启动托盘首读：窗口布局/Dispatcher 已就绪，构造阶段不触碰 explorer。
        Loaded += OnLoaded;

        // 分辨率/缩放变化后重新贴顶（多显示器扩展点：M2.1 只做主屏）
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _themeManager.ThemeChanged += OnThemeChanged;
    }

    private MenuBarFlyoutWindow CreateFlyout()
    {
        // 滑条写回依当前模式分派到音量/亮度，静音按钮固定走音量。
        var viewModel = new MenuBarFlyoutViewModel(
            value =>
            {
                if (_flyoutIsVolume)
                    _viewModel.SetVolumeFromFlyout(value);
                else
                    _viewModel.SetBrightnessFromFlyout(value);
            },
            _viewModel.ToggleMuteFromFlyout,
            () =>
            {
                if (!_flyoutIsVolume)
                    _viewModel.FlushBrightnessWrite();
            });
        var flyout = new MenuBarFlyoutWindow(viewModel);
        flyout.Closed += OnFlyoutClosed;
        return flyout;
    }

    private void OnFlyoutClosed(object? sender, EventArgs e)
    {
        if (sender is MenuBarFlyoutWindow flyout)
            flyout.Closed -= OnFlyoutClosed;

        if (ReferenceEquals(_flyout, sender))
            _flyout = null;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 布局完成后 DPI 时序才稳定；托盘首读仍由 VM 异步投递到后台。
        PositionBar();
        _trayVm.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;

        // 置顶 + 不抢焦点 + 不进 Alt+Tab，与 Dock 完全一致的一套扩展样式
        WindowStyleService.ApplyDockStyles(hwnd);

        if (SystemBackdropService.IsAccentAcrylicSupported)
        {
            ApplyAcrylicTheme(hwnd);
        }

        // 句柄创建后、窗口可见前先落位一次，防止首帧出现在默认位置造成闪跳
        PositionBar();

        // AppBar 注册：让系统保留顶部工作区（失败降级覆盖式，不影响显示）
        if (_reserveWorkArea)
        {
            _appBar.Register(hwnd, GetBarHeightPx());
        }

        // 挂 WndProc 接 AppBar 回调（全屏让位等）
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);
    }

    /// <summary>AppBar / 系统消息回调（含 explorer 重启广播）。</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_appBar.IsRegistered
            && msg == (int)_appBar.CallbackMessage)
        {
            var fullscreen = _appBar.HandleCallback(wParam, lParam);
            if (fullscreen.HasValue)
            {
                // ABN_FULLSCREENAPP：全屏应用出现 → 隐藏让位；退出全屏 → 恢复
                if (fullscreen.Value)
                    HideForFullscreen();
                else
                    RestoreFromFullscreen();

                handled = true;
            }
        }

        // explorer 重启：托盘窗口全部重建，重置并立即重枚举托盘区（消息 ID 为 0 说明注册失败，跳过）
        if (TrayIconReader.TaskbarCreatedMessage != 0
            && msg == (int)TrayIconReader.TaskbarCreatedMessage)
        {
            _trayVm.ResetForExplorerRestart();
            handled = true;
        }

        return IntPtr.Zero;
    }

    /// <summary>托盘图标左键：单击转发左键抬起，双击触发双击消息。</summary>
    private void OnTrayMouseLeft(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TrayIconItem item)
            return;

        // WPF 的 ClickCount 区分单击/双击；双击发 WM_LBUTTONDBLCLK，否则发 WM_LBUTTONUP
        var message = e.ClickCount >= 2
            ? TrayIconForwarder.MouseLeftDoubleClick
            : TrayIconForwarder.MouseLeftButtonUp;
        _trayVm.ForwardClick(item, message);
        e.Handled = true;
    }

    /// <summary>托盘图标右键：转发右键抬起（弹上下文菜单）。</summary>
    private void OnTrayMouseRight(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TrayIconItem item)
            return;

        _trayVm.ForwardClick(item, TrayIconForwarder.MouseRightButtonUp);
        e.Handled = true;
    }

    /// <summary>chevron 点击：弹出/收起溢出图标弹层。</summary>
    private void OnChevronClick(object sender, MouseButtonEventArgs e)
    {
        OverflowPopup.IsOpen = !OverflowPopup.IsOpen;
        e.Handled = true;
    }

    /// <summary>全屏应用出现：隐藏菜单栏（AppBar 保持注册，工作区暂不归还——退出全屏即恢复）。</summary>
    private void HideForFullscreen()
    {
        if (_hiddenForFullscreen)
            return;

        _hiddenForFullscreen = true;
        Logger.Info("检测到全屏应用，菜单栏让位隐藏");
        Hide();
    }

    /// <summary>退出全屏：恢复显示并按当前 DPI 重新申请工作区。</summary>
    private void RestoreFromFullscreen()
    {
        if (!_hiddenForFullscreen)
            return;

        _hiddenForFullscreen = false;
        Logger.Info("全屏应用退出，菜单栏恢复");
        Show();
        PositionBar();
        if (_reserveWorkArea)
            _appBar.UpdatePosition(GetBarHeightPx());
    }

    /// <summary>当前通栏高度（物理像素，随 DPI 变化）。</summary>
    private int GetBarHeightPx()
    {
        var scaleY = PresentationSource.FromVisual(this)?
            .CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        return (int)Math.Ceiling(BarHeight * scaleY);
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

    /// <summary>分辨率/缩放变化后重新贴顶 + 重新申请工作区（回调可能来自非 UI 线程）。</summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RepositionAfterDisplayChange);
            return;
        }

        RepositionAfterDisplayChange();
    }

    /// <summary>显示模式变化后的统一校准：贴顶 + AppBar SETPOS（DPI 变了高度也要重算）。</summary>
    private void RepositionAfterDisplayChange()
    {
        PositionBar();
        if (_reserveWorkArea && _appBar.IsRegistered)
            _appBar.UpdatePosition(GetBarHeightPx());
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (!SystemBackdropService.IsAccentAcrylicSupported)
        {
            ApplyFallbackTheme();
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            ApplyAcrylicTheme(hwnd);
    }

    private void ApplyAcrylicTheme(IntPtr hwnd)
    {
        // Accent 颜色为 ABGR，只更新 MacDock 自己的菜单栏窗口。
        var tint = _themeManager.IsDark ? 0x66202024u : 0xAAF7F2F2u;
        SystemBackdropService.ApplyAccentAcrylic(hwnd, tint);
    }

    private void ApplyFallbackTheme()
    {
        var top = _themeManager.IsDark
            ? Color.FromArgb(0xE0, 0x28, 0x28, 0x2C)
            : Color.FromArgb(0xE8, 0xF7, 0xF8, 0xFC);
        var bottom = _themeManager.IsDark
            ? Color.FromArgb(0xCC, 0x1E, 0x1E, 0x22)
            : Color.FromArgb(0xD8, 0xEA, 0xEE, 0xF6);
        GlassLayer.Background = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(top, 0),
                new GradientStop(bottom, 1),
            },
        };
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

    /// <summary>M5 控制中心按钮：重复点击收起，首次点击创建单例窗口。</summary>
    private void OnControlCenterClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (_controlCenter is { IsOpen: true })
            {
                _controlCenter.Collapse();
                return;
            }

            if (_flyout is { IsOpen: true })
                _flyout.Collapse();
            OverflowPopup.IsOpen = false;

            if (_controlCenter is null)
            {
                var viewModel = new ControlCenterViewModel(_viewModel, _themeManager);
                try
                {
                    _controlCenter = new ControlCenterWindow(viewModel) { Owner = this };
                    _controlCenter.Closed += OnControlCenterClosed;
                }
                catch
                {
                    viewModel.Dispose();
                    throw;
                }
            }

            _controlCenter.ShowBelowAnchor(ComputeAnchor(ControlCenterButton));
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "打开控制中心失败");
        }
        finally
        {
            e.Handled = true;
        }
    }

    private void OnControlCenterClosed(object? sender, EventArgs e)
    {
        if (sender is ControlCenterWindow controlCenter)
            controlCenter.Closed -= OnControlCenterClosed;

        if (ReferenceEquals(_controlCenter, sender))
            _controlCenter = null;
    }

    /// <summary>M5 启动台按钮：全屏展示当前用户可启动的开始菜单和商店应用。</summary>
    private void OnLaunchpadClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            if (_launchpadWindow is not null)
            {
                _launchpadWindow.Activate();
                return;
            }

            if (_flyout is { IsOpen: true })
                _flyout.Collapse();
            _controlCenter?.Collapse();
            OverflowPopup.IsOpen = false;

            var viewModel = new LaunchpadViewModel();
            try
            {
                var launchpad = new LaunchpadWindow(viewModel) { Owner = this };
                _launchpadWindow = launchpad;
                launchpad.Closed += (_, _) => _launchpadWindow = null;
                launchpad.Show();
                launchpad.Activate();
            }
            catch
            {
                viewModel.Dispose();
                throw;
            }
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "打开启动台失败");
        }
        finally
        {
            e.Handled = true;
        }
    }

    /// <summary>音量图标点击：toggle 音量浮窗（已在音量模式则收起；否则切换为音量模式并弹出）。</summary>
    private void OnVolumeClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var flyout = _flyout ??= CreateFlyout();
            if (flyout.IsOpen && _flyoutIsVolume)
            {
                flyout.Collapse();
                return;
            }

            _flyoutIsVolume = true;
            flyout.ViewModel.ShowVolume(
                _viewModel.GetVolumeLevel() ?? 0,
                _viewModel.IsMuted);
            flyout.ShowBelowIcon(ComputeAnchor(VolumeIcon));
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "打开音量浮窗失败");
        }
    }

    /// <summary>亮度图标点击：toggle 亮度浮窗（仅内屏支持时可见）。</summary>
    private void OnBrightnessClick(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var flyout = _flyout ??= CreateFlyout();
            if (flyout.IsOpen && !_flyoutIsVolume)
            {
                flyout.Collapse();
                return;
            }

            _flyoutIsVolume = false;
            // 弹窗首值只读 ViewModel 缓存，不在 UI 线程触发 WMI。
            flyout.ViewModel.ShowBrightness(_viewModel.CachedBrightnessLevel ?? 0);
            flyout.ShowBelowIcon(ComputeAnchor(BrightnessIcon));
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "打开亮度浮窗失败");
        }
    }

    /// <summary>音量图标滚轮：步进 2%（向上+，向下-）。</summary>
    private void OnVolumeWheel(object sender, MouseWheelEventArgs e)
    {
        _viewModel.StepVolume(e.Delta > 0 ? 2 : -2);
        e.Handled = true;
    }

    /// <summary>亮度图标滚轮：步进 5%。</summary>
    private void OnBrightnessWheel(object sender, MouseWheelEventArgs e)
    {
        _viewModel.StepBrightness(e.Delta > 0 ? 5 : -5);
        e.Handled = true;
    }

    /// <summary>轮询刷新后同步已打开的浮窗（外部音量/亮度变化回灌）。</summary>
    private void OnControlsRefreshed()
    {
        if (_flyout is not { IsOpen: true })
            return;

        if (_flyoutIsVolume)
        {
            var volume = _viewModel.GetVolumeLevel() ?? 0;
            _flyout.ViewModel.SetValueFromSystem(
                volume,
                _viewModel.IsMuted,
                MenuBarFlyoutViewModel.VolumeIconState(volume, _viewModel.IsMuted));
        }
        else
        {
            // 低频刷新结果已经由 ViewModel 异步回灌；这里继续只读缓存。
            _flyout.ViewModel.SetValueFromSystem(_viewModel.CachedBrightnessLevel ?? 0);
        }
    }

    /// <summary>计算图标下方的弹出锚点（屏幕物理像素，x=图标中线，y=图标底部）。</summary>
    private Point ComputeAnchor(FrameworkElement icon)
        => icon.PointToScreen(new Point(icon.ActualWidth / 2, icon.ActualHeight));

    protected override void OnClosed(EventArgs e)
    {
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _themeManager.ThemeChanged -= OnThemeChanged;
        _viewModel.ControlsRefreshed -= OnControlsRefreshed;
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
        _appBar.Dispose();
        _trayVm.Dispose();
        _trayReader.Dispose();
        if (_flyout is not null)
        {
            _flyout.Closed -= OnFlyoutClosed;
            _flyout.Close();
            _flyout = null;
        }
        if (_controlCenter is not null)
        {
            _controlCenter.Closed -= OnControlCenterClosed;
            _controlCenter.Close();
            _controlCenter = null;
        }
        _launchpadWindow?.Close();
        _launchpadWindow = null;
        _aboutWindow?.Close();
        _aboutWindow = null;
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
