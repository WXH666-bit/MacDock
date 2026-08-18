using System.Windows;
using MacDock.UI.ViewModels;

namespace MacDock.UI.Views;

/// <summary>
/// 设置窗口：可正常激活（不应用 Dock 的不抢焦点样式）。
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        DataContext = new SettingsViewModel();
    }
}
