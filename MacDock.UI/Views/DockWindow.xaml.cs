using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using H.NotifyIcon.Core;
using MacDock.Core.Services;
using MacDock.Core.Services.Taskbar;
using MacDock.UI.Controls;
using MacDock.UI.ViewModels;
using MacDock.UI.Services;
using NLog;

namespace MacDock.UI.Views;

/// <summary>
/// Dock 图标层窗口：无边框分层透明、置顶、点击不抢焦点、底部居中。
/// 毛玻璃背景由 DockBackdropWindow 承担（双窗口结构，放大的图标可溢出玻璃条上方）。
/// </summary>
public partial class DockWindow : Window
{
    // ---- 布局常量（DIP） ----
    /// <summary>玻璃条横向内边距（静止图标行两侧留白）。</summary>
    private const double BackdropPadX = 14;
    /// <summary>玻璃条距屏幕工作区底边距离。</summary>
    private const double BackdropBottomMargin = 6;
    /// <summary>静止图标底边距玻璃条底边的内缩。</summary>
    private const double IconBottomInset = 7;
    private const int MaxConcurrentMinimizeFlights = 3;
    /// <summary>玻璃条高度 = 图标尺寸 + 2 * IconBottomInset。</summary>
    private static double BackdropHeight(double iconSize) => iconSize + 2 * IconBottomInset;

    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();
    private readonly MainViewModel _viewModel;
    private readonly ShellMessageClassifier _shellMessages;
    private readonly Func<SettingsViewModel> _settingsViewModelFactory;
    private readonly DockBackdropWindow _backdrop;
    private readonly HashSet<MinimizeFlightWindow> _minimizeFlights = [];
    private int _activeMinimizeFlightCount;
    private FishEyePanel? _panel;
    private HwndSource? _messageSource;
    private HwndSourceHook? _shellMessageHook;
    private SettingsWindow? _settingsWindow;
    private bool _closing;

    public event EventHandler? ShellEnvironmentChanged;

    /// <summary>
    /// Dock 持有的窗口监控实例，供菜单栏复用同一份 WinEventHook。
    /// 生命周期归 MainViewModel，调用方不得 Dispose。
    /// </summary>
    public WindowMonitor WindowMonitor => _viewModel.WindowMonitor;

    public DockWindow(
        MainViewModel viewModel,
        ShellMessageClassifier shellMessages,
        Func<SettingsViewModel> settingsViewModelFactory,
        ThemeManager themeManager)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _shellMessages = shellMessages
            ?? throw new ArgumentNullException(nameof(shellMessages));
        _settingsViewModelFactory = settingsViewModelFactory
            ?? throw new ArgumentNullException(nameof(settingsViewModelFactory));
        _backdrop = new DockBackdropWindow(
            themeManager ?? throw new ArgumentNullException(nameof(themeManager)));

        InitializeComponent();
        DataContext = _viewModel;

        _viewModel.LaunchFailed += OnLaunchFailed;
        _viewModel.WindowMonitor.WindowMinimizeStarted += OnWindowMinimizeStarted;

        SizeChanged += (_, _) => PositionDock();
        Loaded += (_, _) =>
        {
            _backdrop.Show();
            PositionDock();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 追加扩展样式：置顶、点击不抢焦点、不进 Alt+Tab
        var handle = new WindowInteropHelper(this).Handle;
        WindowStyleService.ApplyDockStyles(handle);

        if (handle != IntPtr.Zero && _shellMessageHook is null)
        {
            _messageSource = HwndSource.FromHwnd(handle);
            if (_messageSource is not null)
            {
                _shellMessageHook = OnShellMessage;
                _messageSource.AddHook(_shellMessageHook);
            }
        }
    }

    private IntPtr OnShellMessage(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        var messageId = unchecked((uint)message);
        if (_shellMessages.IsShellEnvironmentChange(messageId))
            ShellEnvironmentChanged?.Invoke(this, EventArgs.Empty);

        return IntPtr.Zero;
    }

    /// <summary>鱼眼面板加载完成：保存引用并挂接悬停事件（面板在 ItemsPanelTemplate 内，无法 x:Name）。</summary>
    private void OnFishEyePanelLoaded(object sender, RoutedEventArgs e)
    {
        _panel = (FishEyePanel)sender;
        _panel.HoverChanged += OnHoverChanged;
        PositionDock();
    }

    /// <summary>定位图标层与玻璃条：均底部居中，玻璃条只包静止图标行并压在图标层之下。</summary>
    private void PositionDock()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0 || _panel is null)
            return;

        // 玻璃条：宽 = 静止行净宽 + 两侧留白
        double bw = _panel.StaticContentWidth + 2 * BackdropPadX;
        double bh = BackdropHeight(_panel.IconSize);
        var (bLeft, bTop) = WindowPlacementService.GetBottomCenter(bw, bh, BackdropBottomMargin);
        _backdrop.Left = bLeft;
        _backdrop.Top = bTop;
        _backdrop.Width = bw;
        _backdrop.Height = bh;

        // 图标层：底边 = 玻璃条底边 + 内缩（静止图标坐在玻璃条内）
        var (left, top) = WindowPlacementService.GetBottomCenter(
            ActualWidth, ActualHeight, BackdropBottomMargin + IconBottomInset);
        Left = left;
        Top = top;

        // Z 序：玻璃条贴在图标层正下方
        var self = new WindowInteropHelper(this).Handle;
        if (self != IntPtr.Zero && _backdrop.Handle != IntPtr.Zero)
            WindowStyleService.PlaceBelow(_backdrop.Handle, self);
    }

    // ---- macOS 风格名称气泡 ----
    private void OnHoverChanged(int index, double centerX)
    {
        if (index < 0 || index >= _viewModel.Items.Count)
        {
            FadeBubble(0);
            return;
        }

        NameText.Text = _viewModel.Items[index].Name;
        NameBubble.UpdateLayout();

        if (_panel is not null)
        {
            // 面板坐标 → 气泡画布坐标，气泡居中于悬停图标静止中心
            var anchor = _panel.TranslatePoint(new Point(centerX, 0), LabelCanvas);
            Canvas.SetLeft(NameBubble, anchor.X - NameBubble.ActualWidth / 2.0);
            Canvas.SetTop(NameBubble, LabelCanvas.Height - NameBubble.ActualHeight - 4);
        }

        FadeBubble(1);
    }

    /// <summary>气泡淡入/淡出（120ms）。</summary>
    private void FadeBubble(double to)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        NameBubble.BeginAnimation(OpacityProperty, animation);
    }

    // ---- M4 最小化飞入动画 ----
    private void OnWindowMinimizeStarted(IntPtr hwnd, string exeName)
    {
        if (_closing
            || !WindowSnapshotService.IsAnimationSupported
            || !WindowPlacementService.IsOnPrimaryMonitor(hwnd)
            || Volatile.Read(ref _activeMinimizeFlightCount)
                >= MaxConcurrentMinimizeFlights)
        {
            return;
        }

        // 若调用已在 WPF 线程，先确认 Dock 中已有对应固定或临时项；正常生产路径的
        // WinEvent 位于专用消息线程，只做一次有界 GDI 快照，再回 UI 线程匹配控件。
        if (Dispatcher.CheckAccess()
            && _viewModel.FindItemForProcess(exeName) is null)
        {
            return;
        }

        var snapshot = WindowSnapshotService.TryCapture(hwnd);
        if (snapshot is null)
            return;

        if (Dispatcher.CheckAccess())
        {
            TryPlayMinimizeFlight(snapshot, exeName);
            return;
        }

        try
        {
            Dispatcher.BeginInvoke(
                () => TryPlayMinimizeFlight(snapshot, exeName),
                DispatcherPriority.Render);
        }
        catch (InvalidOperationException)
        {
            // Dispatcher 正在退出；快照只在内存中，由 GC 释放。
        }
    }

    private void TryPlayMinimizeFlight(WindowSnapshot snapshot, string exeName)
    {
        if (_closing
            || Dispatcher.HasShutdownStarted
            || Volatile.Read(ref _activeMinimizeFlightCount)
                >= MaxConcurrentMinimizeFlights
            || _minimizeFlights.Count >= MaxConcurrentMinimizeFlights)
        {
            return;
        }

        var item = _viewModel.FindItemForProcess(exeName);
        if (item is null)
            return;

        var icon = FindDockIcon(item);
        var source = PresentationSource.FromVisual(this);
        if (icon is null || source?.CompositionTarget is null)
            return;

        Rect sourceRect;
        Rect targetRect;
        try
        {
            var fromDevice = source.CompositionTarget.TransformFromDevice;
            sourceRect = RectFromPhysicalPixels(
                new Point(snapshot.Left, snapshot.Top),
                new Point(snapshot.Left + snapshot.Width, snapshot.Top + snapshot.Height),
                fromDevice);

            var targetTopLeft = icon.PointToScreen(new Point(0, 0));
            var targetBottomRight = icon.PointToScreen(
                new Point(icon.ActualWidth, icon.ActualHeight));
            targetRect = RectFromPhysicalPixels(
                targetTopLeft,
                targetBottomRight,
                fromDevice);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        if (sourceRect.Width < 1
            || sourceRect.Height < 1
            || targetRect.Width < 1
            || targetRect.Height < 1)
        {
            return;
        }

        MinimizeFlightWindow? flight = null;
        try
        {
            flight = new MinimizeFlightWindow(
                snapshot.Image,
                sourceRect,
                targetRect);
            flight.Closed += OnMinimizeFlightClosed;
            _minimizeFlights.Add(flight);
            Interlocked.Increment(ref _activeMinimizeFlightCount);
            flight.Show();
        }
        catch (Exception exception)
        {
            if (flight is not null)
            {
                flight.Closed -= OnMinimizeFlightClosed;
                if (_minimizeFlights.Remove(flight))
                    Interlocked.Decrement(ref _activeMinimizeFlightCount);
                flight.Cancel();
            }

            Logger.Debug(exception, "M4 最小化飞行动画创建失败，已使用系统原生最小化");
        }
    }

    private void OnMinimizeFlightClosed(object? sender, EventArgs e)
    {
        if (sender is not MinimizeFlightWindow flight)
            return;

        flight.Closed -= OnMinimizeFlightClosed;
        if (_minimizeFlights.Remove(flight))
            Interlocked.Decrement(ref _activeMinimizeFlightCount);
    }

    private DockIconControl? FindDockIcon(DockItemViewModel item)
        => FindVisualDescendant<DockIconControl>(
            DockItems,
            control => ReferenceEquals(control.DataContext, item));

    private static T? FindVisualDescendant<T>(
        DependencyObject root,
        Func<T, bool> predicate)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match && predicate(match))
                return match;

            var nested = FindVisualDescendant(child, predicate);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    private static Rect RectFromPhysicalPixels(
        Point topLeft,
        Point bottomRight,
        Matrix fromDevice)
    {
        var dipTopLeft = fromDevice.Transform(topLeft);
        var dipBottomRight = fromDevice.Transform(bottomRight);
        return new Rect(dipTopLeft, dipBottomRight);
    }

    // ---- 启动失败通知 ----
    private void OnLaunchFailed(string message)
    {
        try
        {
            TrayIcon.ShowNotification("MacDock", message, NotificationIcon.Error,
                customIconHandle: null, largeIcon: false, sound: true,
                respectQuietTime: true, realtime: false, timeout: null);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "托盘气泡显示失败");
        }
    }

    // ---- 拖入固定 ----
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Any(IsSupportedFile))
            e.Effects = DragDropEffects.Copy;

        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files)
            return;

        foreach (var file in files.Where(IsSupportedFile))
        {
            try
            {
                _viewModel.AddFromPath(file);
                Logger.Info("已固定项目：{0}", file);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "固定项目失败：{0}", file);
            }
        }
    }

    private static bool IsSupportedFile(string path) =>
        string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase);

    // ---- 托盘菜单 ----
    private void OnTrayContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
            return;

        // H.NotifyIcon opens WPF menus at the Shell callback's absolute anchor.
        // Windows 11 can report the notification-area anchor for an icon inside
        // the overflow panel, so use the pointer that actually opened the menu.
        menu.Placement = PlacementMode.MousePoint;
        menu.HorizontalOffset = 0;
        menu.VerticalOffset = 0;
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_settingsViewModelFactory()) { Owner = this };
        _settingsWindow.Closed += OnSettingsWindowClosed;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OnSettingsWindowClosed(object? sender, EventArgs e)
    {
        if (_settingsWindow is not null)
            _settingsWindow.Closed -= OnSettingsWindowClosed;
        _settingsWindow = null;
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        Logger.Info("退出 MacDock");
        Application.Current.Shutdown();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;
        if (_messageSource is not null && _shellMessageHook is not null)
            _messageSource.RemoveHook(_shellMessageHook);

        _shellMessageHook = null;
        _messageSource = null;
        _viewModel.LaunchFailed -= OnLaunchFailed;
        _viewModel.WindowMonitor.WindowMinimizeStarted -= OnWindowMinimizeStarted;
        foreach (var flight in _minimizeFlights.ToArray())
        {
            flight.Closed -= OnMinimizeFlightClosed;
            flight.Cancel();
        }
        _minimizeFlights.Clear();
        Volatile.Write(ref _activeMinimizeFlightCount, 0);
        if (_settingsWindow is not null)
        {
            _settingsWindow.Closed -= OnSettingsWindowClosed;
            _settingsWindow.Close();
            _settingsWindow = null;
        }
        _viewModel.Dispose();
        _backdrop.Close();
        TrayIcon.Dispose();
        base.OnClosed(e);
    }
}
