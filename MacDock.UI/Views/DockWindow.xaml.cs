using System.Windows;
using System.Windows.Interop;
using MacDock.Core.Services;
using MacDock.UI.ViewModels;
using NLog;

namespace MacDock.UI.Views;

/// <summary>
/// Dock 主窗口：无边框透明、置顶、点击不抢焦点、底部居中。
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

        // 尺寸确定后定位到底部居中
        SizeChanged += (_, _) => PositionDock();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        // 追加扩展样式：置顶、点击不抢焦点、不进 Alt+Tab
        WindowStyleService.ApplyDockStyles(hwnd);
    }

    private void PositionDock()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
            return;

        var (left, top) = WindowPlacementService.GetBottomCenter(ActualWidth, ActualHeight, 4);
        Left = left;
        Top = top;
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
        var settings = new SettingsWindow();
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
