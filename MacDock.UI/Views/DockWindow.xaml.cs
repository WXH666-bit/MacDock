using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using H.NotifyIcon.Core;
using MacDock.Core.Services;
using MacDock.Core.Services.Taskbar;
using MacDock.UI.Controls;
using MacDock.UI.ViewModels;
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
    /// <summary>玻璃条高度 = 图标尺寸 + 2 * IconBottomInset。</summary>
    private static double BackdropHeight(double iconSize) => iconSize + 2 * IconBottomInset;

    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();
    private readonly MainViewModel _viewModel;
    private readonly ShellMessageClassifier _shellMessages;
    private readonly Func<SettingsViewModel> _settingsViewModelFactory;
    private readonly DockBackdropWindow _backdrop = new();
    private FishEyePanel? _panel;
    private HwndSource? _messageSource;
    private HwndSourceHook? _shellMessageHook;

    public event EventHandler? ShellEnvironmentChanged;

    public DockWindow(
        MainViewModel viewModel,
        ShellMessageClassifier shellMessages,
        Func<SettingsViewModel> settingsViewModelFactory)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _shellMessages = shellMessages
            ?? throw new ArgumentNullException(nameof(shellMessages));
        _settingsViewModelFactory = settingsViewModelFactory
            ?? throw new ArgumentNullException(nameof(settingsViewModelFactory));

        InitializeComponent();
        DataContext = _viewModel;

        // 托盘图标（M1 使用系统默认图标，后续替换为自定义 .ico）
        TrayIcon.Icon = System.Drawing.SystemIcons.Application;

        _viewModel.LaunchFailed += OnLaunchFailed;

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
    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var settings = new SettingsWindow(_settingsViewModelFactory()) { Owner = this };
        settings.Show();
        settings.Activate();
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        Logger.Info("退出 MacDock");
        Application.Current.Shutdown();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_messageSource is not null && _shellMessageHook is not null)
            _messageSource.RemoveHook(_shellMessageHook);

        _shellMessageHook = null;
        _messageSource = null;
        _viewModel.LaunchFailed -= OnLaunchFailed;
        _viewModel.Dispose();
        _backdrop.Close();
        TrayIcon.Dispose();
        base.OnClosed(e);
    }
}
