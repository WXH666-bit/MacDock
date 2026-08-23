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
    private readonly DispatcherTimer _volumeSelfHealTimer;
    private readonly AudioService _audio;
    private readonly BrightnessService _brightness;
    private readonly object _brightnessWriteGate = new();
    private CancellationTokenSource? _brightnessWriteCts;
    private int _lastBrightnessWrite;
    private int _brightnessReadInFlight;
    private int _foregroundResolveVersion;
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

    /// <summary>是否有可用音频设备（无设备时隐藏喇叭图标，与亮度图标语义一致）。</summary>
    [ObservableProperty]
    private bool _isAudioAvailable = true;

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

        // 音量改事件回调驱动（替代 500ms 轮询）：启动读一次，变化由音量端点通知触发
        _audio.VolumeChanged += OnAudioVolumeChanged;
        RefreshAudioState();
        RefreshVolume();

        UpdateClock();
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _clockTimer.Tick += OnClockTick;
        _clockTimer.Start();

        // 亮度仍是异步 500ms 轮询（WMI 无回调机制）；音量不再进该计时器
        RefreshBrightness();
        _controlsTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _controlsTimer.Tick += OnControlsTick;
        _controlsTimer.Start();

        // 30 秒低频兜底自愈：设备拔插/端点切换等不触发音量回调时，保证图标最终正确
        _volumeSelfHealTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _volumeSelfHealTimer.Tick += OnVolumeSelfHealTick;
        _volumeSelfHealTimer.Start();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _windowMonitor.ForegroundAppChanged -= OnForegroundAppChanged;
        _audio.VolumeChanged -= OnAudioVolumeChanged;
        _clockTimer.Tick -= OnClockTick;
        _clockTimer.Stop();
        _controlsTimer.Tick -= OnControlsTick;
        _controlsTimer.Stop();
        _volumeSelfHealTimer.Tick -= OnVolumeSelfHealTick;
        _volumeSelfHealTimer.Stop();
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

        // 版本号：只允许最新一次前台切换的异步 UWP 完善回写
        var version = Interlocked.Increment(ref _foregroundResolveVersion);

        dispatcher.BeginInvoke(
            () =>
            {
                if (_disposed)
                    return;

                // 先给一个即时值（映射表/标题/进程名，纯快路径），UWP 显示名稍后异步完善
                ForegroundAppName = FormatAppName(processName, windowTitle);
                EnrichWithUwpNameAsync(processName, windowTitle, version);
            },
            DispatcherPriority.Background);
    }

    /// <summary>
    /// 异步完善显示名：UWP 本地化名在后台线程解析（WinRT/Shell 在 UI 线程首触发会卡帧），
    /// 成功且仍是当前前台才回写。未命中或已切换则保持快路径结果。
    /// </summary>
    private void EnrichWithUwpNameAsync(string processName, string? windowTitle, int version)
    {
        // 内置映射表优先于 UWP 显示名；命中映射就不再花 WinRT 去解析
        if (_disposed
            || string.IsNullOrWhiteSpace(windowTitle)
            || AppFriendlyNames.TryGetFriendlyName(processName) is not null)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        _ = Task.Run(() =>
        {
            var aumid = UwpDisplayNameResolver.ResolveAumid(processName);
            if (aumid is null)
                return;

            var uwpName = UwpDisplayNameResolver.GetDisplayName(aumid);
            if (uwpName is null)
                return;

            dispatcher.BeginInvoke(() =>
            {
                if (_disposed || version != _foregroundResolveVersion)
                    return;

                ForegroundAppName = uwpName;
            }, DispatcherPriority.Background);
        });
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

        // 快路径：内置映射表 > 窗口标题 > 进程名（UWP 本地化名由异步完善，避免 WinRT 卡 UI）
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

    /// <summary>500ms 轮询：亮度（异步） + 音频设备存在性。音量走回调，不进这里。</summary>
    private void OnControlsTick(object? sender, EventArgs e)
    {
        RefreshAudioState();
        RefreshBrightness();
        ControlsRefreshed?.Invoke();
    }

    /// <summary>音量变化回调（COM 原生线程）：回 UI 线程刷新音量与浮窗同步。</summary>
    private void OnAudioVolumeChanged()
    {
        if (_disposed)
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        dispatcher.BeginInvoke(
            () =>
            {
                if (_disposed)
                    return;

                RefreshAudioState();
                RefreshVolume();
                ControlsRefreshed?.Invoke();
            },
            DispatcherPriority.Background);
    }

    /// <summary>30 秒低频兜底：设备切换等不触发回调时维持图标正确。</summary>
    private void OnVolumeSelfHealTick(object? sender, EventArgs e)
    {
        RefreshAudioState();
        RefreshVolume();
        ControlsRefreshed?.Invoke();
    }

    private void RefreshAudioState()
    {
        IsAudioAvailable = _audio.IsAvailable;
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

    /// <summary>
    /// 亮度状态异步刷新：WMI 查询放到后台线程，回到 UI 线程更新绑定属性。
    /// 单飞行标志避免读回乱序（上一次未返回时不发新一轮）。
    /// </summary>
    private void RefreshBrightness()
    {
        // 上一次异步读还没回来就跳过本轮，防止拿到过期值覆盖
        if (Interlocked.Exchange(ref _brightnessReadInFlight, 1) == 1)
            return;

        _ = Task.Run(() =>
        {
            var available = _brightness.IsAvailable;
            var level = available ? _brightness.GetBrightness() : null;
            return (Available: available, Level: level);
        }).ContinueWith(t =>
        {
            Interlocked.Exchange(ref _brightnessReadInFlight, 0);
            if (_disposed || t.IsFaulted)
                return;

            var result = t.Result;
            IsBrightnessAvailable = result.Available;
            BrightnessPercent = result.Available && result.Level.HasValue
                ? $"{result.Level.Value}%"
                : null;
        }, TaskScheduler.FromCurrentSynchronizationContext());
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

    /// <summary>
    /// 浮窗滑条写回亮度：WMI 写走后台线程 + 单飞行最新值覆盖（拖动进度的最后一次写才落盘，
    /// 中间值由取消令牌跳过，不卡 UI）。
    /// </summary>
    public void SetBrightnessFromFlyout(double value)
    {
        WriteBrightnessAsync((int)Math.Round(value));
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

        WriteBrightnessAsync(Math.Clamp(level.Value + delta, 0, 100));
    }

    /// <summary>亮度异步写：单飞行 + 最新值覆盖（取消上一次未开始的写，拖动不卡 UI）。</summary>
    private void WriteBrightnessAsync(int level)
    {
        Volatile.Write(ref _lastBrightnessWrite, level);

        CancellationTokenSource? prior;
        CancellationToken token;
        lock (_brightnessWriteGate)
        {
            prior = _brightnessWriteCts;
            _brightnessWriteCts = new CancellationTokenSource();
            token = _brightnessWriteCts.Token;
        }

        prior?.Cancel();
        prior?.Dispose();

        _ = Task.Run(() =>
        {
            if (token.IsCancellationRequested)
                return;

            _brightness.SetBrightness(Volatile.Read(ref _lastBrightnessWrite));
        }, token);
    }
}
