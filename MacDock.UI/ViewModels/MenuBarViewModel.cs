using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MacDock.Core.Services;
using MacDock.UI.Views;

namespace MacDock.UI.ViewModels;

/// <summary>
/// 顶部菜单栏视图模型：左侧前台应用名、右侧时间 + 音量/亮度控制区。
/// 前台应用数据源复用 MainViewModel 持有的 WindowMonitor（不重复挂钩）；
/// 音量走 Core Audio，亮度走 WMI，二者均由 500ms 轮询刷新状态。
/// </summary>
public sealed partial class MenuBarViewModel : ObservableObject, IDisposable
{
    /// <summary>无前台应用（如刚启动、桌面）时显示的兜底名。</summary>
    private const string FallbackAppName = "MacDock";

    private readonly WindowMonitor _windowMonitor;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _controlsTimer;
    private readonly AudioService _audio;
    private readonly BrightnessService _brightness;
    private bool _disposed;

    /// <summary>当前前台应用显示名（窗口标题优先，退化为进程名）。</summary>
    [ObservableProperty]
    private string _foregroundAppName = FallbackAppName;

    /// <summary>当前时间文本，格式「周X M月d日 HH:mm」。</summary>
    [ObservableProperty]
    private string _clockText = string.Empty;

    /// <summary>音量百分比文本（如 "50%"）；不可用时为空。</summary>
    [ObservableProperty]
    private string _volumePercent = string.Empty;

    /// <summary>喇叭图标状态键（speaker_0/1/2/3），由 UI 映射矢量路径。</summary>
    [ObservableProperty]
    private string _volumeIconState = "speaker_3";

    /// <summary>是否静音。</summary>
    [ObservableProperty]
    private bool _isMuted;

    /// <summary>亮度百分比文本（如 "60%"）；不支持时为 null/空。</summary>
    [ObservableProperty]
    private string? _brightnessPercent;

    /// <summary>当前环境是否支持亮度控制（否则隐藏亮度图标）。</summary>
    [ObservableProperty]
    private bool _isBrightnessAvailable;

    /// <summary>音量/亮度刷新完成后触发（供浮窗等界面做外部变化二次同步）。</summary>
    public event Action? ControlsRefreshed;

    /// <param name="windowMonitor">共享的窗口监控实例，生命周期由调用方持有。</param>
    public MenuBarViewModel(WindowMonitor windowMonitor)
        : this(windowMonitor, new AudioService(), new BrightnessService())
    {
    }

    /// <summary>供单测注入假音频/亮度服务。</summary>
    internal MenuBarViewModel(
        WindowMonitor windowMonitor,
        AudioService audio,
        BrightnessService brightness)
    {
        _windowMonitor = windowMonitor ?? throw new ArgumentNullException(nameof(windowMonitor));
        _audio = audio;
        _brightness = brightness;

        // 启动时取一次当前前台应用，避免等到第一次切换才有内容
        var current = _windowMonitor.GetForegroundApp();
        if (current is not null)
            ForegroundAppName = FormatAppName(current.Value.ProcessName, current.Value.WindowTitle);

        _windowMonitor.ForegroundAppChanged += OnForegroundAppChanged;

        UpdateClock();
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _clockTimer.Tick += OnClockTick;
        _clockTimer.Start();

        // 音量/亮度 500ms 刷新（覆盖 Fn 键等外部变化，音量/亮度图标与其保持同步）
        RefreshControls();
        _controlsTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _controlsTimer.Tick += OnControlsTick;
        _controlsTimer.Start();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _windowMonitor.ForegroundAppChanged -= OnForegroundAppChanged;
        _clockTimer.Tick -= OnClockTick;
        _clockTimer.Stop();
        _controlsTimer.Tick -= OnControlsTick;
        _controlsTimer.Stop();
        _audio.Dispose();
    }

    /// <summary>WindowMonitor 回调在原生线程，须回 UI 线程更新绑定属性。</summary>
    private void OnForegroundAppChanged(string processName, string? windowTitle)
    {
        if (_disposed)
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        dispatcher.BeginInvoke(
            () =>
            {
                if (!_disposed)
                    ForegroundAppName = FormatAppName(processName, windowTitle);
            },
            DispatcherPriority.Background);
    }

    /// <summary>
    /// 显示名优先级：进程名中文映射 > 主窗口标题 > 进程名。
    /// 未映射进程显示窗口标题（M2.2 引入映射表，UWP 包显示名留 M2.3）。
    /// </summary>
    internal static string FormatAppName(string processName, string? windowTitle)
    {
        // 内部系统进程（dwm 等）不显示——回到兜底名，由 WindowMonitor/订阅方决定是否隐藏
        if (AppFriendlyNames.IsIgnored(processName))
            return FallbackAppName;

        var friendly = AppFriendlyNames.TryGetFriendlyName(processName);
        if (friendly is not null)
            return friendly;

        if (!string.IsNullOrWhiteSpace(windowTitle))
            return windowTitle.Trim();

        return string.IsNullOrWhiteSpace(processName) ? FallbackAppName : processName;
    }

    private void OnClockTick(object? sender, EventArgs e) => UpdateClock();

    private void UpdateClock() => ClockText = FormatClock(DateTime.Now, CultureInfo.CurrentCulture);

    /// <summary>
    /// 生成时间文本，格式「周X M月d日 HH:mm」（如「周日 8月23日 16:38」）。
    /// 非中文区域改用该区域自带的月日格式，避免硬编码中文的「月/日」。
    /// </summary>
    internal static string FormatClock(DateTime now, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        // "ddd" 在中文区域给出「周日」，英文区域给出「Sun」，各自符合习惯
        var weekday = now.ToString("ddd", culture);
        var date = culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? now.ToString(@"M月d日", culture)
            : now.ToString(culture.DateTimeFormat.MonthDayPattern, culture);
        var time = now.ToString("HH:mm", culture);

        return $"{weekday} {date} {time}";
    }

    /// <summary>音量/亮度轮询刷新。所有读取失败都降压可见，不抛异常。</summary>
    private void OnControlsTick(object? sender, EventArgs e) => RefreshControls();

    private void RefreshControls()
    {
        RefreshVolume();
        RefreshBrightness();
        ControlsRefreshed?.Invoke();
    }

    private void RefreshVolume()
    {
        var volume = _audio.GetVolume();
        if (volume.HasValue)
        {
            var pct = (int)Math.Round(volume.Value * 100);
            VolumePercent = $"{pct}%";
            IsMuted = _audio.GetMute() ?? false;
            VolumeIconState = MenuBarFlyoutViewModel.VolumeIconState(pct, IsMuted);
        }
        else
        {
            VolumePercent = string.Empty;
            VolumeIconState = "speaker_3";
        }
    }

    private void RefreshBrightness()
    {
        var available = _brightness.IsAvailable;
        IsBrightnessAvailable = available;
        if (!available)
        {
            BrightnessPercent = null;
            return;
        }

        var brightness = _brightness.GetBrightness();
        BrightnessPercent = brightness.HasValue ? $"{brightness.Value}%" : null;
    }

    /// <summary>取当前音量（0-100）；不可用时返回 null。</summary>
    public int? GetVolumeLevel()
    {
        var volume = _audio.GetVolume();
        return volume.HasValue ? (int)Math.Round(volume.Value * 100) : null;
    }

    /// <summary>取当前亮度（0-100）；不可用时返回 null。</summary>
    public int? GetBrightnessLevel()
    {
        var brightness = _brightness.GetBrightness();
        return brightness;
    }

    /// <summary>浮窗滑条写回音量（由菜单栏窗口转交）。</summary>
    public void SetVolumeFromFlyout(double value)
    {
        _audio.SetVolume((float)(value / 100.0));
    }

    /// <summary>浮窗滑条写回亮度（由菜单栏窗口转交）。</summary>
    public void SetBrightnessFromFlyout(double value)
    {
        _brightness.SetBrightness((int)Math.Round(value));
    }

    /// <summary>切换静音（浮窗静音按钮）。</summary>
    public void ToggleMuteFromFlyout()
    {
        var mute = _audio.GetMute();
        _audio.SetMute(!(mute ?? false));
    }

    /// <summary>音量滚轮步进（步长 2%，越界自动截断）。返回是否成功。</summary>
    public void StepVolume(int delta)
    {
        var volume = GetVolumeLevel();
        if (!volume.HasValue)
            return;

        _audio.SetVolume((float)Math.Clamp(volume.Value + delta, 0, 100) / 100f);
    }

    /// <summary>亮度滚轮步进（步长 5%，越界自动截断）。返回是否成功。</summary>
    public void StepBrightness(int delta)
    {
        if (!_brightness.IsAvailable)
            return;

        var level = GetBrightnessLevel();
        if (!level.HasValue)
            return;

        _brightness.SetBrightness(Math.Clamp(level.Value + delta, 0, 100));
    }
}
