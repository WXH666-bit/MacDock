using System.Windows.Controls;
using System.Windows.Input;
using MacDock.Animations;

namespace MacDock.UI.Controls;

/// <summary>
/// Dock 单个图标控件：squircle 底板 + 图标，左键启动（带弹跳反馈）、右键移除。
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
        BounceAnimation.Play(SquircleBorder);
    }
}
