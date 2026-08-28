using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MacDock.Animations;
using MacDock.UI.ViewModels;

namespace MacDock.UI.Controls;

/// <summary>
/// Dock 单个图标控件：图标 + macOS 风格运行指示小圆点，左键启动（带弹跳反馈）、右键移除。
/// </summary>
public partial class DockIconControl : UserControl
{
    public DockIconControl()
    {
        InitializeComponent();

        // 按下即弹跳，作为启动的视觉反馈（启动命令由 InputBindings 在抬起时触发）
        PreviewMouseLeftButtonDown += OnPreviewLeftButtonDown;
    }

    private void OnPreviewLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is DockItemViewModel { IsSeparator: true })
            return;

        BounceAnimation.Play(SquircleBorder);
    }
}
