using CommunityToolkit.Mvvm.ComponentModel;
using MacDock.Core.Services;

namespace MacDock.UI.ViewModels;

/// <summary>
/// 设置窗口视图模型：开机自启开关。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isAutoStart;

    public SettingsViewModel()
    {
        _isAutoStart = AutoStartService.IsEnabled();
    }

    partial void OnIsAutoStartChanged(bool value)
    {
        AutoStartService.SetEnabled(value);
    }
}
