using System.Windows;
using System.Windows.Media.Animation;
using MacDock.UI.ViewModels;

namespace MacDock.UI.Views;

/// <summary>
/// 设置窗口：可正常激活（不应用 Dock 的不抢焦点样式），打开时淡入。
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;

        Opacity = 0;
        Loaded += (_, _) =>
        {
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
            BeginAnimation(OpacityProperty, fadeIn);
        };
    }
}
