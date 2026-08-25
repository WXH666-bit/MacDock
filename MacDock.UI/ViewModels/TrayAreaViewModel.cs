using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MacDock.Core.Models;
using MacDock.Core.Services;
using NLog;

namespace MacDock.UI.ViewModels;

/// <summary>
/// 菜单栏托盘区视图模型：维护可见托盘图标与溢出图标的集合。
/// DispatcherTimer 只提交刷新请求；探测、全量读取和图标转换全部在后台线程执行。
/// 点击转发由窗口代码通过 <see cref="ForwardClick"/> 完成。
/// </summary>
public sealed partial class TrayAreaViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 内容变化兜底全量间隔。计数节流只对增删有效，故需要低频全量刷新感知图标内容变化。
    /// </summary>
    private static readonly TimeSpan FullRefreshInterval = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(10),
    };

    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

    private readonly ITrayIconReader _reader;
    private readonly Func<IntPtr, ImageSource> _iconFactory;
    private readonly DispatcherTimer _timer;
    private readonly Dispatcher _dispatcher;
    private readonly Func<Action, bool> _postToUi;
    private readonly Func<DateTime> _utcNow;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private readonly bool _enabled;

    // Refresh state is shared by the UI request path and the background worker.
    private int _refreshInFlight;
    private int _refreshPending;
    private int _disposed;
    private int _started;
    private int _needsFullRefresh = 1;
    private int _failureStreak;
    private long _refreshGeneration;
    private long _lastFullRefreshUtcTicks = DateTime.MinValue.Ticks;
    private long _nextAttemptUtcTicks = DateTime.MinValue.Ticks;
    private uint _lastVisibleCount = uint.MaxValue;
    private uint _lastOverflowCount = uint.MaxValue;

    /// <summary>可见托盘图标（menu bar 横排）。</summary>
    public ObservableCollection<TrayIconItem> Visible { get; } = new();

    /// <summary>溢出托盘图标（chevron 弹层内）。</summary>
    public ObservableCollection<TrayIconItem> Overflow { get; } = new();

    /// <summary>是否有溢出图标需要显示 chevron。</summary>
    [ObservableProperty]
    private bool _hasOverflow;

    /// <summary>是否接管托盘（TrayTakeover 开关，false 时整个托盘区隐藏）。</summary>
    [ObservableProperty]
    private bool _isTrayEnabled;

    /// <summary>最近一次刷新是否处于降级状态。</summary>
    [ObservableProperty]
    private bool _isRefreshDegraded;

    /// <summary>最近一次失败的可观测原因；成功后清空。</summary>
    [ObservableProperty]
    private string? _lastRefreshError;

    /// <param name="reader">托盘读取器（生命周期由调用方持有）。</param>
    /// <param name="enabled">是否接管（TrayTakeover=false 时不渲染托盘区）。</param>
    /// <param name="iconFactory">HICON → ImageSource 转换（默认 IconService.FromHIcon）。</param>
    public TrayAreaViewModel(
        ITrayIconReader reader,
        bool enabled,
        Func<IntPtr, ImageSource>? iconFactory = null)
        : this(reader, enabled, iconFactory, postToUi: null, utcNow: null)
    {
    }

    /// <summary>供确定性单测注入 UI 投递器与时钟；生产代码使用窗口 Dispatcher 和 UTC 时钟。</summary>
    internal TrayAreaViewModel(
        ITrayIconReader reader,
        bool enabled,
        Func<IntPtr, ImageSource>? iconFactory,
        Func<Action, bool>? postToUi,
        Func<DateTime>? utcNow)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _enabled = enabled;
        _iconFactory = iconFactory ?? IconService.FromHIcon;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _postToUi = postToUi ?? PostToDispatcher;
        _shutdownToken = _shutdown.Token;

        IsTrayEnabled = enabled;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = ProbeInterval,
        };
        _timer.Tick += OnProbeTick;
    }

    /// <summary>启动轮询与异步首次加载。重复调用不会重复启动。</summary>
    public void Start()
    {
        if (!_enabled)
        {
            Visible.Clear();
            Overflow.Clear();
            HasOverflow = false;
            return;
        }

        if (Interlocked.Exchange(ref _started, 1) != 0 || IsDisposed)
            return;

        _timer.Start();
        RequestRefresh();
    }

    /// <summary>explorer 重启（TaskbarCreated 广播）后调用：重置计数并异步重枚举托盘区。</summary>
    public void ResetForExplorerRestart()
    {
        if (!_enabled || IsDisposed)
            return;

        Interlocked.Increment(ref _refreshGeneration);
        Volatile.Write(ref _lastVisibleCount, uint.MaxValue);
        Volatile.Write(ref _lastOverflowCount, uint.MaxValue);
        Volatile.Write(ref _lastFullRefreshUtcTicks, DateTime.MinValue.Ticks);
        Volatile.Write(ref _needsFullRefresh, 1);
        // 新 explorer 是新的故障域；不能沿用旧进程留下的最长 10 秒退避。
        Volatile.Write(ref _failureStreak, 0);
        Volatile.Write(ref _nextAttemptUtcTicks, DateTime.MinValue.Ticks);
        IsTrayEnabled = true;
        if (Volatile.Read(ref _started) == 0)
            return;

        _timer.Start();
        RequestRefresh();
    }

    /// <summary>转发一次托盘点击（调用方已把鼠标消息映射为 mouseMessage）。</summary>
    public void ForwardClick(TrayIconItem item, uint mouseMessage)
    {
        if (item?.Info is null)
            return;

        TrayIconForwarder.SendClick(
            item.Info.HwndTarget,
            item.Info.UCallbackMessage,
            item.Info.UId,
            mouseMessage);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _timer.Tick -= OnProbeTick;
        _timer.Stop();
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    /// <summary>测试用：提交一次与生产相同的后台刷新请求，不启动 DispatcherTimer。</summary>
    internal Task RequestRefreshForTests() => RequestRefresh();

    /// <summary>测试用：当前连续失败次数。</summary>
    internal int FailureStreakForTests => Volatile.Read(ref _failureStreak);

    /// <summary>测试用：当前失败次数对应的退避时长；成功复位后为 500ms。</summary>
    internal TimeSpan RetryDelayForTests
    {
        get
        {
            var streak = Volatile.Read(ref _failureStreak);
            return streak <= 0
                ? ProbeInterval
                : RetryDelays[Math.Min(streak, RetryDelays.Length) - 1];
        }
    }

    /// <summary>测试用：下一次实际尝试的时间。</summary>
    internal DateTime NextAttemptUtcForTests
        => new(Volatile.Read(ref _nextAttemptUtcTicks), DateTimeKind.Utc);

    /// <summary>测试用：当前是否已有后台刷新。</summary>
    internal bool RefreshInFlightForTests => Volatile.Read(ref _refreshInFlight) != 0;

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private void OnProbeTick(object? sender, EventArgs e)
    {
        // UI 线程只发请求；所有 Probe/Read/iconFactory 调用都在 RequestRefresh 的后台任务中。
        RequestRefresh();
    }

    private Task RequestRefresh()
    {
        if (!_enabled || IsDisposed)
            return Task.CompletedTask;

        var nowTicks = _utcNow().Ticks;
        if (nowTicks < Volatile.Read(ref _nextAttemptUtcTicks))
            return Task.CompletedTask;

        // 单飞：不等待、不加锁。Explorer 重启等强制刷新会留下 pending，旧扫描结束后立即补跑。
        if (Interlocked.CompareExchange(ref _refreshInFlight, 1, 0) != 0)
        {
            Volatile.Write(ref _refreshPending, 1);
            return Task.CompletedTask;
        }

        var generation = Volatile.Read(ref _refreshGeneration);
        return ExecuteRefreshAsync(generation, _shutdownToken);
    }

    private async Task ExecuteRefreshAsync(long generation, CancellationToken cancellationToken)
    {
        try
        {
            var result = await Task.Run(
                    () => BuildRefreshResult(generation, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);

            if (IsDisposed)
            {
                ReleaseRefresh();
                return;
            }

            if (!_postToUi(() => ApplyRefreshResult(result)))
                ReleaseRefresh();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ReleaseRefresh();
        }
        catch (Exception exception)
        {
            if (IsDisposed)
            {
                ReleaseRefresh();
                return;
            }

            var failure = RefreshWorkResult.Failure(
                generation,
                $"后台托盘刷新异常：{exception.GetType().Name}: {exception.Message}");
            try
            {
                if (!_postToUi(() => ApplyRefreshResult(failure)))
                    ReleaseRefresh();
            }
            catch
            {
                ReleaseRefresh();
            }
        }
    }

    private RefreshWorkResult BuildRefreshResult(long generation, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var visibleCount = _reader.ProbeVisibleCount();
            cancellationToken.ThrowIfCancellationRequested();
            var overflowCount = _reader.ProbeOverflowCount();

            var countsChanged = visibleCount != Volatile.Read(ref _lastVisibleCount)
                || (overflowCount.HasValue
                    && overflowCount.Value != Volatile.Read(ref _lastOverflowCount));
            var lastFullRefresh = new DateTime(
                Volatile.Read(ref _lastFullRefreshUtcTicks),
                DateTimeKind.Utc);
            var dueForFull = _utcNow() - lastFullRefresh >= FullRefreshInterval;
            var needsFullRefresh = Volatile.Read(ref _needsFullRefresh) != 0;

            if (!needsFullRefresh && !countsChanged && !dueForFull)
                return RefreshWorkResult.NoChange(generation);

            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = _reader.Read();
            cancellationToken.ThrowIfCancellationRequested();

            var visible = new List<TrayIconItem>();
            var overflow = new List<TrayIconItem>();
            foreach (var info in snapshot.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // IconService.FromHIcon 必须留在后台；其结果在进入集合前已 Freeze。
                var item = new TrayIconItem(_iconFactory(info.HIcon), info);
                if (info.IsOverflow)
                    overflow.Add(item);
                else
                    visible.Add(item);
            }

            return RefreshWorkResult.Success(
                generation,
                visibleCount,
                snapshot.OverflowAvailable
                    ? overflowCount ?? (uint)overflow.Count
                    : null,
                visible,
                snapshot.OverflowAvailable ? overflow : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return RefreshWorkResult.Failure(
                generation,
                $"{exception.GetType().Name}: {exception.Message}",
                stopUntilExplorerRestart: exception is TrayIconSessionUnavailableException);
        }
    }

    private void ApplyRefreshResult(RefreshWorkResult result)
    {
        try
        {
            if (IsDisposed || result.Generation != Volatile.Read(ref _refreshGeneration))
                return;

            switch (result.Kind)
            {
                case RefreshWorkKind.NoChange:
                    return;

                case RefreshWorkKind.Success:
                    if (result.VisibleItems is null)
                        throw new InvalidOperationException("托盘刷新成功结果缺少可见集合");

                    Volatile.Write(ref _lastVisibleCount, result.VisibleCount);
                    Volatile.Write(ref _lastFullRefreshUtcTicks, _utcNow().Ticks);
                    Volatile.Write(ref _needsFullRefresh, 0);
                    ApplyDiff(Visible, result.VisibleItems);
                    if (result.OverflowItems is not null && result.OverflowCount.HasValue)
                    {
                        Volatile.Write(ref _lastOverflowCount, result.OverflowCount.Value);
                        ApplyDiff(Overflow, result.OverflowItems);
                        HasOverflow = Overflow.Count > 0;
                    }
                    else
                    {
                        // 溢出弹层尚未创建：保留上一次结果，待窗口重新出现时强制刷新。
                        Volatile.Write(ref _lastOverflowCount, uint.MaxValue);
                    }

                    MarkRefreshSuccess();
                    return;

                case RefreshWorkKind.Failure:
                    Volatile.Write(ref _needsFullRefresh, 1);
                    if (result.StopUntilExplorerRestart)
                        DisableForCurrentExplorer(result.Error ?? "当前 explorer 会话不支持安全托盘枚举");
                    else
                        MarkRefreshFailure(result.Error ?? "未知托盘刷新失败");
                    return;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _needsFullRefresh, 1);
            MarkRefreshFailure($"应用托盘刷新结果失败：{exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            ReleaseRefresh();
        }
    }

    private void MarkRefreshSuccess()
    {
        Volatile.Write(ref _failureStreak, 0);
        Volatile.Write(ref _nextAttemptUtcTicks, _utcNow().Add(ProbeInterval).Ticks);
        IsRefreshDegraded = false;
        LastRefreshError = null;
    }

    private void MarkRefreshFailure(string error)
    {
        var streak = Math.Min(Volatile.Read(ref _failureStreak) + 1, RetryDelays.Length);
        Volatile.Write(ref _failureStreak, streak);
        var delay = RetryDelays[streak - 1];
        Volatile.Write(ref _nextAttemptUtcTicks, _utcNow().Add(delay).Ticks);
        IsRefreshDegraded = true;
        LastRefreshError = error;
        Logger.Warn("托盘区刷新失败（连续第 {0} 次），{1} 后重试：{2}", streak, delay, error);
    }

    private void DisableForCurrentExplorer(string error)
    {
        _timer.Stop();
        Volatile.Write(ref _nextAttemptUtcTicks, DateTime.MaxValue.Ticks);
        // 会话级故障意味着 HWND/HICON 可能已经失效。隐藏并清掉可点击副本，等待
        // TaskbarCreated 后重新获取；保留陈旧按钮会把用户输入转发给错误或已销毁的窗口。
        Visible.Clear();
        Overflow.Clear();
        HasOverflow = false;
        IsTrayEnabled = false;
        IsRefreshDegraded = true;
        LastRefreshError = error;
        Logger.Warn("当前 explorer 会话无法安全枚举托盘，本次会话已停止探测：{0}", error);
    }

    private void ReleaseRefresh()
    {
        Interlocked.Exchange(ref _refreshInFlight, 0);
        if (Interlocked.Exchange(ref _refreshPending, 0) != 0 && !IsDisposed)
            RequestRefresh();
    }

    private bool PostToDispatcher(Action action)
    {
        if (IsDisposed || _dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
            return false;

        _dispatcher.BeginInvoke(DispatcherPriority.Background, action);
        return true;
    }

    /// <summary>
    /// 差量更新集合：移除已消失的，加入新的，替换内容变化的，保留未变的。
    /// 内容变化：Key 相同但 <see cref="TrayIconInfo.HIcon"/> 或 <see cref="TrayIconInfo.Tooltip"/>
    /// 不同（应用 NIM_MODIFY 换图标）→ 整项替换。用索引 setter 触发 ObservableCollection 的
    /// Replace 通知，只刷新这一项，其余不动不闪。
    /// </summary>
    internal static void ApplyDiff(ObservableCollection<TrayIconItem> current, List<TrayIconItem> fresh)
    {
        var freshByKey = new Dictionary<string, TrayIconItem>(fresh.Count);
        foreach (var item in fresh)
            freshByKey[item.Info.Key] = item;

        for (int i = current.Count - 1; i >= 0; i--)
        {
            if (!freshByKey.ContainsKey(current[i].Info.Key))
                current.RemoveAt(i);
        }

        for (int i = 0; i < current.Count; i++)
        {
            var kept = current[i];
            if (!freshByKey.TryGetValue(kept.Info.Key, out var replacement))
                continue;

            var freshInfo = replacement.Info;
            if (freshInfo.HIcon != kept.Info.HIcon
                || !string.Equals(freshInfo.Tooltip, kept.Info.Tooltip, StringComparison.Ordinal))
            {
                current[i] = replacement;
            }
        }

        var currentKeys = new HashSet<string>(current.Select(i => i.Info.Key));
        foreach (var item in fresh.OrderBy(i => i.Info.Key, StringComparer.Ordinal))
        {
            if (!currentKeys.Contains(item.Info.Key))
                current.Add(item);
        }
    }

    private enum RefreshWorkKind
    {
        NoChange,
        Success,
        Failure,
    }

    private sealed record RefreshWorkResult(
        long Generation,
        RefreshWorkKind Kind,
        uint VisibleCount,
        uint? OverflowCount,
        List<TrayIconItem>? VisibleItems,
        List<TrayIconItem>? OverflowItems,
        string? Error,
        bool StopUntilExplorerRestart)
    {
        public static RefreshWorkResult NoChange(long generation)
            => new(generation, RefreshWorkKind.NoChange, 0, 0, null, null, null, false);

        public static RefreshWorkResult Success(
            long generation,
            uint visibleCount,
            uint? overflowCount,
            List<TrayIconItem> visibleItems,
            List<TrayIconItem>? overflowItems)
            => new(
                generation,
                RefreshWorkKind.Success,
                visibleCount,
                overflowCount,
                visibleItems,
                overflowItems,
                null,
                false);

        public static RefreshWorkResult Failure(
            long generation,
            string error,
            bool stopUntilExplorerRestart = false)
            => new(
                generation,
                RefreshWorkKind.Failure,
                0,
                0,
                null,
                null,
                error,
                stopUntilExplorerRestart);
    }
}
