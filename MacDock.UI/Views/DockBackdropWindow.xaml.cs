using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using MacDock.Core.Services;
using MacDock.UI.Services;

namespace MacDock.UI.Views;

/// <summary>
/// Dock 背景玻璃条窗口：无边框、置顶、鼠标穿透，仅做视觉。
/// Win11 22H2+ 走 Accent 亚克力 + DWM 圆角；更低版本降级为分层窗口半透明自绘。
/// </summary>
public partial class DockBackdropWindow : Window
{
    private readonly ThemeManager _themeManager;

    public DockBackdropWindow(ThemeManager themeManager)
    {
        _themeManager = themeManager ?? throw new ArgumentNullException(nameof(themeManager));
        InitializeComponent();

        // AllowsTransparency 必须在句柄创建前设置，降级判断放构造函数
        if (!SystemBackdropService.IsAcrylicSupported)
            ApplyLegacyFallback();

        _themeManager.ThemeChanged += OnThemeChanged;
    }

    /// <summary>窗口句柄（用于 Z 序编排；未创建时为 IntPtr.Zero）。</summary>
    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        // 置顶、不抢焦点、不进 Alt+Tab，并附加鼠标穿透（交互全部由图标层承担）
        WindowStyleService.ApplyDockStyles(hwnd, clickThrough: true);

        if (SystemBackdropService.IsAcrylicSupported)
        {
            SystemBackdropService.ApplyRoundedCorners(hwnd);
            ApplyAcrylicTheme(hwnd);
        }
    }

    /// <summary>Win11 22H2 以下降级：分层窗口 + 半透明渐变 + 阴影。</summary>
    private void ApplyLegacyFallback()
    {
        AllowsTransparency = true;
        GlassBorder.Margin = new Thickness(24);
        GlassBorder.SetResourceReference(
            System.Windows.Controls.Border.BackgroundProperty,
            "DockBackgroundBrush");
        GlassBorder.Effect = new DropShadowEffect
        {
            BlurRadius = 20,
            ShadowDepth = 2,
            Opacity = 0.30,
            Color = Colors.Black,
        };
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (!SystemBackdropService.IsAcrylicSupported)
            return;

        var hwnd = Handle;
        if (hwnd != IntPtr.Zero)
            ApplyAcrylicTheme(hwnd);
    }

    private void ApplyAcrylicTheme(IntPtr hwnd)
    {
        // Accent 颜色为 ABGR。仅修改 MacDock 自己的背景窗口，不触碰系统主题。
        var tint = _themeManager.IsDark ? 0x55202024u : 0x88F7F2F2u;
        SystemBackdropService.ApplyAccentAcrylic(hwnd, tint);
    }

    protected override void OnClosed(EventArgs e)
    {
        _themeManager.ThemeChanged -= OnThemeChanged;
        base.OnClosed(e);
    }
}
