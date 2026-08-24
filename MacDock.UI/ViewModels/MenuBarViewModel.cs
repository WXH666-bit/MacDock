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
/// 音量走 Core Audio（事件回调驱动，5s 低频自愈重绑），亮度走 WMI（2s 异步轮询）。
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
    private readonly LatestValueAsyncWriter _brightnessWriter;
    private readonly Dispatcher? _dispatcher;
    private Task _brightnessShutdownCompletion = Task.CompletedTask;
    private int _brightnessReadInFlight;
    private int _cachedBrightnessLevel = -1;
    private bool _cachedBrightnessAvailable;
    private long _brightnessWriteVersion;
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

    /// <summary>供窗口退出路径做有界等待，确保最后一次亮度写尽量落地。</summary>
    internal Task BrightnessShutdownCompletion => _brightnessShutdownCompletion;

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
        _dispatcher = Application.Current?.Dispatcher;
        _brightnessWriter = new LatestValueAsyncWriter(
            (level, cancellationToken) => _brightness.SetBrightnessAsync(level, cancellationToken),
            TimeSpan.FromMilliseconds(80));

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

        // 亮度仍需异步轮询（WMI 无回调机制）；降到 2 秒，避免持续唤醒 WMI provider。
        RefreshBrightness();
        _controlsTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _controlsTimer.Tick += OnControlsTick;
        _controlsTimer.Start();

        // 5 秒低频兜底自愈。正统解法是 RegisterEndpointNotificationCallback(IMMNotificationClient)
        // 事件驱动检测设备切换，但需手写完整 COM 回调接口与 CCW 生命周期；修复轮用 5s 轮询比对
        // 当前默认端点 ID，不一致即重绑，事件驱动留作后续优化。
        _volumeSelfHealTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5),
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
        _brightnessWriter.Dispose();
        _brightnessShutdownCompletion = FinishBrightnessShutdownAsync();
        _audio.Dispose();
    }

    private async Task FinishBrightnessShutdownAsync()
    {
        try
        {
            // 正常情况下只需几十毫秒；硬上限避免退出被异常 WMI 拖住。
            await _brightnessWriter.Completion
                .WaitAsync(TimeSpan.FromMilliseconds(2500))
                .ConfigureAwait(false);
        }
        catch
        {
            _brightnessWriter.Abort();
            try
            {
                await _brightnessWriter.Completion
                    .WaitAsync(TimeSpan.FromMilliseconds(500))
                    .ConfigureAwait(false);
            }
            catch
            {
                // BrightnessService.Dispose 仍会让监督 worker 立即停止等待底层 COM。
            }
        }
        finally
        {
            _brightness.Dispose();
        }
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

    /// <summary>5 秒低频兜底：先确保通知源仍绑定当前默认设备，再读值刷新维持图标正确。</summary>
    private void OnVolumeSelfHealTick(object? sender, EventArgs e)
    {
        // 设备切换/未注册时内部重绑；失败不抛（内部静默）。Rebind 后新端点音量值可能不同，
        // 随后的读值刷新正好把新状态带出来。
        _audio.EnsureVolumeNotifierHealthy();
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
    /// 亮度状态异步刷新：WMI 查询放到 BrightnessService 的串行 worker，
    /// 回到 UI 线程更新缓存与绑定属性。单飞行标志避免读回乱序。
    /// </summary>
    private void RefreshBrightness()
    {
        if (_disposed || Interlocked.Exchange(ref _brightnessReadInFlight, 1) == 1)
            return;

        var writeVersion = Volatile.Read(ref _brightnessWriteVersion);
        _ = RefreshBrightnessAsync(writeVersion);
    }

    private async Task RefreshBrightnessAsync(long writeVersion)
    {
        try
        {
            var available = await _brightness.IsAvailableAsync().ConfigureAwait(false);
            var level = available
                ? await _brightness.GetBrightnessAsync().ConfigureAwait(false)
                : null;

            var dispatcher = _dispatcher;
            if (_disposed || dispatcher is null)
                return;

            void ApplyResult()
            {
                // 写入请求在读取期间发生时，丢弃这次可能过期的读结果。
                if (_disposed || writeVersion != Volatile.Read(ref _brightnessWriteVersion))
                    return;

                _cachedBrightnessAvailable = available;
                _cachedBrightnessLevel = level ?? -1;
                IsBrightnessAvailable = available;
                BrightnessPercent = available && level.HasValue
                    ? $"{level.Value}%"
                    : null;
            }

            if (dispatcher.CheckAccess())
                ApplyResult();
            else
                await dispatcher.InvokeAsync(ApplyResult, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
            // 服务在退出期间停止排队，取消只代表本次刷新无结果。
        }
        catch
        {
            // BrightnessService 已将 WMI 失败收敛为默认值；UI 关闭竞态不应冒泡。
        }
        finally
        {
            Interlocked.Exchange(ref _brightnessReadInFlight, 0);
        }
    }

    /// <summary>取当前音量（0-100）；不可用时返回 null。</summary>
    public int? GetVolumeLevel()
    {
        var volume = _audio.GetVolume();
        return volume.HasValue ? (int)Math.Round(volume.Value * 100) : null;
    }

    /// <summary>当前缓存亮度（0-100）；不会触发同步 WMI 查询。</summary>
    public int? CachedBrightnessLevel
        => _cachedBrightnessAvailable && _cachedBrightnessLevel >= 0
            ? _cachedBrightnessLevel
            : null;

    /// <summary>兼容现有调用方：只返回缓存，不触发同步 WMI 查询。</summary>
    public int? GetBrightnessLevel() => CachedBrightnessLevel;

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
        QueueBrightnessWrite((int)Math.Round(value));
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
        if (!_cachedBrightnessAvailable)
            return;

        var level = CachedBrightnessLevel;
        if (!level.HasValue)
            return;

        QueueBrightnessWrite(Math.Clamp(level.Value + delta, 0, 100));
    }

    /// <summary>亮度异步写：有界单消费者 + 最新值覆盖 + 静默期去抖。</summary>
    private void QueueBrightnessWrite(int level)
    {
        if (_disposed)
            return;

        level = Math.Clamp(level, 0, 100);
        Interlocked.Increment(ref _brightnessWriteVersion);

        // 用户操作本身发生在 UI 线程：先更新缓存，避免下一次浮窗打开或刷新又显示旧值。
        if (_cachedBrightnessAvailable)
        {
            _cachedBrightnessLevel = level;
            BrightnessPercent = $"{level}%";
        }

        _brightnessWriter.Enqueue(level);
    }

    /// <summary>滑块松开时跳过去抖等待，尽快提交用户最后选择的值。</summary>
    internal void FlushBrightnessWrite()
    {
        if (!_disposed)
            _brightnessWriter.Flush();
    }
}

/// <summary>
/// 亮度写入的单消费者队列：有界、最新值覆盖、静默期去抖。
/// 不与 WMI provider 并行；Dispose 后不接收新值，只做一次有界的最终值收尾。
/// </summary>
internal sealed class LatestValueAsyncWriter : IDisposable
{
    private const int MaximumAttemptsPerValue = 2;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    private readonly Func<int, CancellationToken, Task<bool>> _writeAsync;
    private readonly TimeSpan _debounce;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly Task _worker;
    private int _latestValue;
    private long _version;
    private bool _hasPending;
    private bool _signalArmed;
    private bool _flushRequested;
    private bool _completionRequested;
    private CancellationTokenSource? _debounceWakeup;
    private int _abortRequested;
    private int _disposed;

    public LatestValueAsyncWriter(
        Func<int, CancellationToken, Task<bool>> writeAsync,
        TimeSpan debounce)
    {
        _writeAsync = writeAsync ?? throw new ArgumentNullException(nameof(writeAsync));
        _debounce = debounce > TimeSpan.Zero
            ? debounce
            : throw new ArgumentOutOfRangeException(nameof(debounce));
        _worker = Task.Run(ProcessAsync);
    }

    internal Task Completion => _worker;

    public void Enqueue(int value)
    {
        lock (_sync)
        {
            if (_disposed != 0)
                return;

            _latestValue = value;
            _hasPending = true;
            _version++;
            _flushRequested = false;
            if (!_signalArmed)
            {
                _signalArmed = true;
                // 与 Dispose 共用同一把锁，避免 Dispose 让 worker 先释放信号量后这里再 Release。
                _signal.Release();
            }
        }
    }

    /// <summary>跳过当前静默期；用于滑块松开，避免窗口紧接着关闭时丢掉最后值。</summary>
    public void Flush()
    {
        CancellationTokenSource? debounceWakeup;
        lock (_sync)
        {
            if (_disposed != 0 || !_hasPending)
                return;

            _flushRequested = true;
            debounceWakeup = _debounceWakeup;
            if (!_signalArmed)
            {
                _signalArmed = true;
                _signal.Release();
            }
        }

        try
        {
            debounceWakeup?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 静默期恰好自然结束；worker 会在版本检查时看到 flush 标记。
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? debounceWakeup;
        lock (_sync)
        {
            if (_disposed != 0)
                return;

            _disposed = 1;
            _completionRequested = true;
            _flushRequested = true;
            debounceWakeup = _debounceWakeup;
            if (!_signalArmed)
            {
                _signalArmed = true;
                _signal.Release();
            }
        }

        try
        {
            // 正常 Dispose 跳过去抖并排空最后值；真正卡住时由 Abort 的硬上限收敛。
            debounceWakeup?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal void Abort()
    {
        if (Interlocked.Exchange(ref _abortRequested, 1) != 0)
            return;

        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // worker 已在正常完成竞态中释放资源。
        }
    }

    private async Task ProcessAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_cancellation.Token).ConfigureAwait(false);

                var failedAttempts = 0;
                var failedVersion = -1L;
                while (true)
                {
                    lock (_sync)
                    {
                        if (!_hasPending)
                        {
                            _signalArmed = false;
                            break;
                        }
                    }

                    var quietVersion = await WaitForQuietVersionAsync().ConfigureAwait(false);

                    int value;
                    lock (_sync)
                    {
                        if (!_hasPending || quietVersion != _version)
                            continue;

                        value = _latestValue;
                        _flushRequested = false;
                    }

                    if (failedVersion != quietVersion)
                    {
                        // 重试预算属于具体值版本；新值不能继承旧值已经消耗的次数。
                        failedVersion = quietVersion;
                        failedAttempts = 0;
                    }

                    var succeeded = false;
                    try
                    {
                        // WaitAsync 让 Dispose 能停止 writer；BrightnessService 自身仍追踪实际 WMI 操作。
                        var writeTask = _writeAsync(value, _cancellation.Token);
                        succeeded = await writeTask
                            .WaitAsync(_cancellation.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                    {
                        return;
                    }
                    catch
                    {
                        // 与返回 false 一样走一次有界重试；不能让异常终止整个 writer。
                    }

                    var shouldRetry = false;
                    lock (_sync)
                    {
                        if (quietVersion != _version)
                        {
                            // 写入期间又来了更新：旧结果不能清掉真正的最终值。
                            failedVersion = -1;
                            failedAttempts = 0;
                            continue;
                        }

                        if (succeeded)
                        {
                            _hasPending = false;
                            _signalArmed = false;
                            break;
                        }

                        failedAttempts++;
                        shouldRetry = failedAttempts < MaximumAttemptsPerValue;
                        if (!shouldRetry)
                        {
                            // 保留 pending 值；下一次 Enqueue/Flush 可再次尝试，但本轮不持续轰炸 WMI。
                            _signalArmed = false;
                        }
                    }

                    if (shouldRetry)
                    {
                        await Task.Delay(RetryDelay, _cancellation.Token).ConfigureAwait(false);
                        continue;
                    }

                    break;
                }

                lock (_sync)
                {
                    if (_completionRequested)
                        return;
                }
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            // 正常 Dispose 路径。
        }
        finally
        {
            _signal.Dispose();
            _cancellation.Dispose();
        }
    }

    private async Task<long> WaitForQuietVersionAsync()
    {
        while (true)
        {
            long quietVersion;
            CancellationTokenSource debounceWakeup;
            lock (_sync)
            {
                if (_flushRequested)
                    return _version;

                quietVersion = _version;
                debounceWakeup = CancellationTokenSource.CreateLinkedTokenSource(
                    _cancellation.Token);
                _debounceWakeup = debounceWakeup;
            }

            try
            {
                await Task.Delay(_debounce, debounceWakeup.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!_cancellation.IsCancellationRequested)
            {
                // Flush 取消静默期；下面读取受锁保护的最终版本。
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_debounceWakeup, debounceWakeup))
                        _debounceWakeup = null;
                }

                debounceWakeup.Dispose();
            }

            lock (_sync)
            {
                if (_flushRequested || quietVersion == _version)
                    return _version;
            }
        }
    }
}
