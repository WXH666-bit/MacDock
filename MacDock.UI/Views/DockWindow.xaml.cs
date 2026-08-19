using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using H.NotifyIcon.Core;
using MacDock.Core.Services;
using MacDock.UI.ViewModels;
using NLog;

namespace MacDock.UI.Views;

/// <summary>
/// Dock 主窗口：无边框、置顶、点击不抢焦点、底部居中。
/// Win11 22H2+ 走 DWM 亚克力 + 系统圆角；Win10 降级为分层窗口半透明渐变。
/// </summary>
public partial class DockWindow : Window
{
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();
    private readonly MainViewModel _viewModel = new();

    public DockWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        // 托盘图标（M1 使用系统默认图标，后续替换为自定义 .ico）
        TrayIcon.Icon = System.Drawing.SystemIcons.Application;

        _viewModel.LaunchFailed += OnLaunchFailed;

        if (!SystemBackdropService.IsAcrylicSupported)
            ApplyLegacyFallback();

        // 尺寸确定后定位到底部居中
        SizeChanged += (_, _) => PositionDock();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        // 追加扩展样式：置顶、点击不抢焦点、不进 Alt+Tab
        WindowStyleService.ApplyDockStyles(hwnd);

        if (SystemBackdropService.IsAcrylicSupported)
        {
            SystemBackdropService.ApplyRoundedCorners(hwnd);
            SystemBackdropService.ApplyAcrylic(hwnd);
        }
    }

    /// <summary>
    /// Win10（build &lt; 22621）降级：SYSTEMBACKDROP 不可用，切回分层窗口半透明渐变。
    /// AllowsTransparency 必须在句柄创建前设置，故放在构造函数中调用。
    /// </summary>
    private void ApplyLegacyFallback()
    {
        AllowsTransparency = true;
        RootGrid.Margin = new Thickness(26); // 容纳降级阴影的 BlurRadius(22) + 4
        DockBorder.CornerRadius = new CornerRadius(20);
        DockBorder.Background = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x8C, 0xF9, 0xF9, 0xFA), 0),
                new GradientStop(Color.FromArgb(0x59, 0xED, 0xED, 0xF0), 1),
            },
        };
        DockBorder.Effect = new DropShadowEffect
        {
            BlurRadius = 22,
            ShadowDepth = 2,
            Opacity = 0.25,
            Color = Colors.Black,
        };
    }

    private void PositionDock()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var (left, top) = WindowPlacementService.GetBottomCenter(ActualWidth, ActualHeight, 4);
        Left = left;
        Top = top;
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
        var settings = new SettingsWindow { Owner = this };
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
        base.OnClosed(e);
        TrayIcon.Dispose();
    }
}
