using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using MacDock.Core.Services;

namespace MacDock.UI.Views;

/// <summary>
/// Dock 背景玻璃条窗口：无边框、置顶、鼠标穿透，仅做视觉。
/// Win11 22H2+ 走 Accent 亚克力 + DWM 圆角；更低版本降级为分层窗口半透明自绘。
/// </summary>
public partial class DockBackdropWindow : Window
{
    public DockBackdropWindow()
    {
        InitializeComponent();

        // AllowsTransparency 必须在句柄创建前设置，降级判断放构造函数
        if (!SystemBackdropService.IsAcrylicSupported)
            ApplyLegacyFallback();
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
            // 中性深灰约 33% 不透明：保住毛玻璃通透感，同时压暗背景保证图标可读
            SystemBackdropService.ApplyAccentAcrylic(hwnd, 0x55202024);
        }
    }

    /// <summary>Win11 22H2 以下降级：分层窗口 + 半透明渐变 + 阴影。</summary>
    private void ApplyLegacyFallback()
    {
        AllowsTransparency = true;
        GlassBorder.Margin = new Thickness(24);
        GlassBorder.Background = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x9E, 0x2C, 0x2C, 0x30), 0),
                new GradientStop(Color.FromArgb(0x8A, 0x22, 0x22, 0x26), 1),
            },
        };
        GlassBorder.Effect = new DropShadowEffect
        {
            BlurRadius = 20,
            ShadowDepth = 2,
            Opacity = 0.30,
            Color = Colors.Black,
        };
    }
}
