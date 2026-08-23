using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;

namespace MacDock.Core.Services.Taskbar;

internal interface IWatchdogEvent : IDisposable
{
    bool WaitOne(TimeSpan timeout);

    void Set();
}

public sealed class TaskbarWatchdogClient : ITaskbarRecoveryGuard
{
    private static readonly TimeSpan ReadyPollInterval = TimeSpan.FromMilliseconds(25);

    private readonly string _watchdogPath;
    private readonly string _appDataRoot;
    private readonly IWatchdogProcessLauncher _launcher;
    private readonly Func<string, IWatchdogEvent> _eventFactory;
    private readonly object _stateGate = new();

    private WatchdogClientState _state = WatchdogClientState.Idle;
    private ActiveSession? _activeSession;
    private TaskCompletionSource<bool>? _armCompletion;
    private TaskCompletionSource<bool>? _disarmCompletion;

    public TaskbarWatchdogClient(string watchdogPath)
        : this(
            watchdogPath,
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            new WatchdogProcessLauncher())
    {
    }

    public TaskbarWatchdogClient(
        string watchdogPath,
        IWatchdogProcessLauncher launcher)
        : this(
            watchdogPath,
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            launcher)
    {
    }

    public TaskbarWatchdogClient(
        string watchdogPath,
        string appDataRoot,
        IWatchdogProcessLauncher launcher)
        : this(
            watchdogPath,
            appDataRoot,
            launcher,
            CreateManualResetEvent)
    {
    }

    internal TaskbarWatchdogClient(
        string watchdogPath,
        string appDataRoot,
        IWatchdogProcessLauncher launcher,
        Func<string, IWatchdogEvent> eventFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(watchdogPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataRoot);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(eventFactory);

        _watchdogPath = watchdogPath;
        _appDataRoot = appDataRoot;
        _launcher = launcher;
        _eventFactory = eventFactory;
    }

    public async Task<TaskbarRecoveryGuardSession?> ArmAsync(
        TaskbarRecoveryGuardRequest request,
        TimeSpan readyTimeout,
        CancellationToken cancellationToken)
    {
        ValidateTimeout(readyTimeout, nameof(readyTimeout));

        while (true)
        {
            TaskCompletionSource<bool>? waitForDisarm = null;
            TaskCompletionSource<bool>? waitForArm = null;
            TaskCompletionSource<bool>? armCompletion = null;

            lock (_stateGate)
            {
                switch (_state)
                {
                    case WatchdogClientState.Idle:
                        armCompletion = CreateCompletionSource();
                        _armCompletion = armCompletion;
                        _state = WatchdogClientState.Arming;
                        break;
                    case WatchdogClientState.Arming:
                        waitForArm = _armCompletion;
                        break;
                    case WatchdogClientState.Disarming:
                        waitForDisarm = _disarmCompletion;
                        break;
                    case WatchdogClientState.Armed:
                    case WatchdogClientState.Disposed:
                        return null;
                }
            }

            if (armCompletion is not null)
            {
                ArmResult? result = null;
                try
                {
                    result = await ArmCoreAsync(
                            request,
                            readyTimeout,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    PublishArmResult(armCompletion, result);
                }

                return result?.PublicSession;
            }

            if (waitForArm is not null)
            {
                if (!await WaitForCompletionOrCancellationAsync(
                        waitForArm.Task,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    return null;
                }

                return null;
            }

            if (waitForDisarm is not null)
            {
                if (!await WaitForCompletionOrCancellationAsync(
                        waitForDisarm.Task,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    return null;
                }

                continue;
            }
        }
    }

    public async Task DisarmAsync(
        string leaseId,
        TimeSpan exitTimeout,
        CancellationToken cancellationToken)
    {
        ValidateTimeout(exitTimeout, nameof(exitTimeout));

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            TaskCompletionSource<bool>? waitForArm = null;
            TaskCompletionSource<bool>? waitForDisarm = null;
            TaskCompletionSource<bool>? disarmCompletion = null;
            ActiveSession? session = null;

            lock (_stateGate)
            {
                switch (_state)
                {
                    case WatchdogClientState.Idle:
                    case WatchdogClientState.Disposed:
                        return;
                    case WatchdogClientState.Arming:
                        waitForArm = _armCompletion;
                        break;
                    case WatchdogClientState.Disarming:
                        waitForDisarm = _disarmCompletion;
                        break;
                    case WatchdogClientState.Armed:
                        if (_activeSession is null
                            || !string.Equals(
                                _activeSession.LeaseId,
                                leaseId,
                                StringComparison.Ordinal))
                        {
                            return;
                        }

                        session = _activeSession;
                        disarmCompletion = CreateCompletionSource();
                        _disarmCompletion = disarmCompletion;
                        _state = WatchdogClientState.Disarming;
                        break;
                }
            }

            if (disarmCompletion is not null && session is not null)
            {
                Exception? failure = null;
                try
                {
                    await DisarmCoreAsync(session, exitTimeout).ConfigureAwait(false);
                    DisposeSession(session);
                }
                catch (Exception exception)
                {
                    failure = exception;
                }

                PublishDisarmCompletion(disarmCompletion, failure);
                if (failure is not null)
                {
                    ExceptionDispatchInfo.Capture(failure).Throw();
                }

                return;
            }

            if (waitForArm is not null)
            {
                if (!await WaitForCompletionOrCancellationAsync(
                        waitForArm.Task,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    return;
                }

                if (cancellationToken.IsCancellationRequested)
                    return;
                continue;
            }

            if (waitForDisarm is not null)
            {
                if (!await WaitForCompletionOrCancellationAsync(
                        waitForDisarm.Task,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    return;
                }

                continue;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        while (true)
        {
            TaskCompletionSource<bool>? waitForArm = null;
            TaskCompletionSource<bool>? waitForDisarm = null;
            ActiveSession? session = null;
            var disposedWithoutSession = false;

            lock (_stateGate)
            {
                switch (_state)
                {
                    case WatchdogClientState.Disposed:
                        return;
                    case WatchdogClientState.Arming:
                        waitForArm = _armCompletion;
                        break;
                    case WatchdogClientState.Disarming:
                        waitForDisarm = _disarmCompletion;
                        break;
                    case WatchdogClientState.Idle:
                        _state = WatchdogClientState.Disposed;
                        disposedWithoutSession = true;
                        break;
                    case WatchdogClientState.Armed:
                        session = _activeSession;
                        _activeSession = null;
                        _state = WatchdogClientState.Disposed;
                        break;
                }
            }

            if (disposedWithoutSession)
                return;

            if (session is not null)
            {
                DisposeSession(session);
                return;
            }

            if (waitForArm is not null)
            {
                await waitForArm.Task.ConfigureAwait(false);
                continue;
            }

            if (waitForDisarm is not null)
            {
                await waitForDisarm.Task.ConfigureAwait(false);
                continue;
            }
        }
    }

    private async Task<ArmResult?> ArmCoreAsync(
        TaskbarRecoveryGuardRequest request,
        TimeSpan readyTimeout,
        CancellationToken cancellationToken)
    {
        IWatchdogEvent? readyEvent = null;
        IWatchdogEvent? stopEvent = null;
        IWatchdogProcess? process = null;
        var retained = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var eventToken = Guid.NewGuid().ToString("N");
            var readyEventName = $"Local\\MacDock.Taskbar.{eventToken}.ready";
            var stopEventName = $"Local\\MacDock.Taskbar.{eventToken}.stop";
            var arguments = BuildArguments(request, readyEventName, stopEventName);
            if (!TaskbarWatchdogOptions.TryParse(
                    arguments,
                    _appDataRoot,
                    out var parsedOptions,
                    out _)
                || parsedOptions is null)
            {
                return null;
            }

            arguments[7] = parsedOptions.JournalPath;
            readyEvent = _eventFactory(readyEventName);
            stopEvent = _eventFactory(stopEventName);
            process = _launcher.Start(_watchdogPath, arguments);
            if (process is null || process.Id <= 0)
                return null;

            if (!await WaitForReadyAsync(
                    readyEvent,
                    process,
                    readyTimeout,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
                return null;

            var activeSession = new ActiveSession(
                parsedOptions.LeaseId,
                process,
                readyEvent,
                stopEvent);
            retained = true;
            return new ArmResult(
                activeSession,
                new TaskbarRecoveryGuardSession(process.Id));
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (!retained)
            {
                if (process is not null)
                    TryTerminate(process);
                readyEvent?.Dispose();
                stopEvent?.Dispose();
                process?.Dispose();
            }
        }
    }

    private async Task DisarmCoreAsync(
        ActiveSession session,
        TimeSpan exitTimeout)
    {
        session.StopEvent.Set();
        var exited = await WaitForExitAsync(session.Process, exitTimeout)
            .ConfigureAwait(false);
        if (!exited)
            session.Process.Terminate();
    }

    private void PublishArmResult(
        TaskCompletionSource<bool> completion,
        ArmResult? result)
    {
        lock (_stateGate)
        {
            if (!ReferenceEquals(_armCompletion, completion))
                return;

            _armCompletion = null;
            if (result is null)
            {
                _state = WatchdogClientState.Idle;
            }
            else
            {
                _activeSession = result.ActiveSession;
                _state = WatchdogClientState.Armed;
            }
        }

        completion.TrySetResult(true);
    }

    private void PublishDisarmCompletion(
        TaskCompletionSource<bool> completion,
        Exception? failure)
    {
        lock (_stateGate)
        {
            if (!ReferenceEquals(_disarmCompletion, completion))
                return;

            _disarmCompletion = null;
            if (failure is null)
            {
                _activeSession = null;
                _state = WatchdogClientState.Idle;
            }
            else
            {
                _state = WatchdogClientState.Armed;
            }
        }

        completion.TrySetResult(failure is null);
    }

    private static async Task<bool> WaitForCompletionOrCancellationAsync(
        Task completion,
        CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            await completion.ConfigureAwait(false);
            return true;
        }

        var cancellation = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var completed = await Task.WhenAny(completion, cancellation)
            .ConfigureAwait(false);
        return ReferenceEquals(completed, completion);
    }

    private static string[] BuildArguments(
        TaskbarRecoveryGuardRequest request,
        string readyEventName,
        string stopEventName)
        =>
        [
            "--parent-pid",
            request.OwnerProcessId.ToString(CultureInfo.InvariantCulture),
            "--parent-start-ticks",
            request.OwnerProcessStartTimeUtcTicks.ToString(CultureInfo.InvariantCulture),
            "--lease-id",
            request.LeaseId,
            "--journal",
            request.JournalPath,
            "--ready-event",
            readyEventName,
            "--stop-event",
            stopEventName,
        ];

    private static IWatchdogEvent CreateManualResetEvent(string name)
    {
        var handle = new EventWaitHandle(
            initialState: false,
            EventResetMode.ManualReset,
            name,
            out var createdNew);
        if (!createdNew)
        {
            handle.Dispose();
            throw new IOException("The watchdog handshake event name was already in use.");
        }

        return new NamedWatchdogEvent(handle);
    }

    private static async Task<bool> WaitForReadyAsync(
        IWatchdogEvent readyEvent,
        IWatchdogProcess process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
        => await Task.Run(
                () => WaitForReady(readyEvent, process, timeout, cancellationToken),
                CancellationToken.None)
            .ConfigureAwait(false);

    private static bool WaitForReady(
        IWatchdogEvent readyEvent,
        IWatchdogProcess process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
                return false;

            if (timeout != Timeout.InfiniteTimeSpan
                && stopwatch.Elapsed >= timeout)
            {
                return false;
            }

            var waitTime = timeout == Timeout.InfiniteTimeSpan
                ? ReadyPollInterval
                : RemainingWaitTime(timeout - stopwatch.Elapsed);
            if (readyEvent.WaitOne(waitTime))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return !process.HasExited;
            }
        }
    }

    private static async Task<bool> WaitForExitAsync(
        IWatchdogProcess process,
        TimeSpan timeout)
        => await Task.Run(
                () => process.WaitForExit(timeout),
                CancellationToken.None)
            .ConfigureAwait(false);

    private static TimeSpan RemainingWaitTime(TimeSpan remaining)
        => remaining <= ReadyPollInterval ? remaining : ReadyPollInterval;

    private static void TryTerminate(IWatchdogProcess process)
    {
        try
        {
            process.Terminate();
        }
        catch
        {
        }
    }

    private static void DisposeSession(ActiveSession session)
    {
        session.ReadyEvent.Dispose();
        session.StopEvent.Dispose();
        session.Process.Dispose();
    }

    private static TaskCompletionSource<bool> CreateCompletionSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(parameterName);
    }

    private enum WatchdogClientState
    {
        Idle,
        Arming,
        Armed,
        Disarming,
        Disposed,
    }

    private sealed record ArmResult(
        ActiveSession ActiveSession,
        TaskbarRecoveryGuardSession PublicSession);

    private sealed record ActiveSession(
        string LeaseId,
        IWatchdogProcess Process,
        IWatchdogEvent ReadyEvent,
        IWatchdogEvent StopEvent);

    private sealed class NamedWatchdogEvent : IWatchdogEvent
    {
        private readonly EventWaitHandle _handle;

        public NamedWatchdogEvent(EventWaitHandle handle)
        {
            _handle = handle;
        }

        public bool WaitOne(TimeSpan timeout)
            => _handle.WaitOne(timeout);

        public void Set()
            => _handle.Set();

        public void Dispose()
            => _handle.Dispose();
    }
}
