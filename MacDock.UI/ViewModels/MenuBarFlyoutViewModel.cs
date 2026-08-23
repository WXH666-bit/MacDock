using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MacDock.UI.ViewModels;

/// <summary>
/// 菜单栏浮窗视图模型（音量/亮度共用一个浮窗实例，切换内容）。
/// 滑条绑定 <see cref="Value"/>；拖动时经 <see cref="_onValueChanged"/> 回调写回系统，
/// 外部系统值变化（轮询/Fn 键）经 <see cref="SetValueFromSystem"/> 反哺，不触发回调。
/// </summary>
public sealed partial class MenuBarFlyoutViewModel : ObservableObject
{
    private readonly Action<double>? _onValueChanged;
    private readonly Action? _onToggleMute;
    private bool _applyingSystemValue;
    private bool _userInteracting;

    /// <summary>浮窗标题（音量 / 亮度）。</summary>
    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>滑条值（0.0-100.0）。</summary>
    [ObservableProperty]
    private double _value;

    /// <summary>是否需要静音按钮（音量模式才有）。</summary>
    [ObservableProperty]
    private bool _showMuteButton;

    /// <summary>当前是否静音。</summary>
    [ObservableProperty]
    private bool _isMuted;

    /// <summary>当前显示的图标状态（speaker_0/1/2/3 或 sun），由 UI 映射成矢量路径。</summary>
    [ObservableProperty]
    private string _iconState = "sun";

    /// <summary>浮窗是否处于音量模式。</summary>
    [ObservableProperty]
    private bool _isVolumeMode;

    /// <param name="onValueChanged">滑条值变化回调（用户拖动）。</param>
    /// <param name="onToggleMute">静音切换回调。可为 null（亮度模式无静音）。</param>
    public MenuBarFlyoutViewModel(Action<double>? onValueChanged, Action? onToggleMute)
    {
        _onValueChanged = onValueChanged;
        _onToggleMute = onToggleMute;
    }

    /// <summary>
    /// 切换到音量模式并同步初值。外部系统值回灌输时走 <see cref="SetValueFromSystem"/>。
    /// </summary>
    public void ShowVolume(double value, bool muted)
        => Configure(
            title: "音量",
            value: Math.Clamp(value, 0, 100),
            muted: muted,
            showMute: true,
            iconState: VolumeIconState(value, muted),
            volumeMode: true);

    /// <summary>切换到亮度模式并同步初值。</summary>
    public void ShowBrightness(double value)
        => Configure(
            title: "亮度",
            value: Math.Clamp(value, 0, 100),
            muted: false,
            showMute: false,
            iconState: "sun",
            volumeMode: false);

    /// <summary>滑条绑定写回时触发（用户拖动 / 系统回灌都会经过）。系统回灌由 guard 短路。</summary>
    partial void OnValueChanged(double value)
    {
        if (_applyingSystemValue)
            return;

        _onValueChanged?.Invoke(Math.Clamp(value, 0, 100));
    }

    /// <summary>静音切换：调回调，并由 UI/轮询后续刷新状态。</summary>
    [RelayCommand]
    private void ToggleMute() => _onToggleMute?.Invoke();

    /// <summary>
    /// 系统音量/亮度变化时回灌（不触发 <see cref="OnValueChanged"/> 回调）。
    /// 用户正在拖动滑条时跳过，避免外部轮询值与拖动打架。
    /// </summary>
    public void SetValueFromSystem(double value, bool? muted = null, string? iconState = null)
    {
        if (_userInteracting)
            return;

        _applyingSystemValue = true;
        try
        {
            Value = Math.Clamp(value, 0, 100);

            if (muted.HasValue && IsVolumeMode)
                IsMuted = muted.Value;

            if (iconState is not null)
                IconState = iconState;
        }
        finally
        {
            _applyingSystemValue = false;
        }
    }

    private void Configure(string title, double value, bool muted, bool showMute, string iconState, bool volumeMode)
    {
        _applyingSystemValue = true;
        try
        {
            Title = title;
            IsVolumeMode = volumeMode;
            ShowMuteButton = showMute;
            IsMuted = muted;
            Value = value;
            IconState = iconState;
        }
        finally
        {
            _applyingSystemValue = false;
        }
    }

    /// <summary>进入用户交互（拖动滑条）：期间屏蔽外部系统值回灌输。</summary>
    public void BeginUserInput() => _userInteracting = true;

    /// <summary>结束用户交互。</summary>
    public void EndUserInput() => _userInteracting = false;

    /// <summary>根据音量与静音状态映射喇叭四态图标键。</summary>
    internal static string VolumeIconState(double volume, bool muted)
    {
        if (muted || volume <= 0)
            return "speaker_0";
        if (volume < 34)
            return "speaker_1";
        if (volume < 67)
            return "speaker_2";
        return "speaker_3";
    }
}
