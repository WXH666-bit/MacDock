namespace MacDock.Core.Services.Taskbar;

public enum TaskbarWatchdogSignal
{
    Stop,
    OwnerExited,
}

public interface ITaskbarWatchdogRuntime
{
    void SignalReady();

    Task<TaskbarWatchdogSignal> WaitForStopOrOwnerExitAsync(
        CancellationToken cancellationToken);
}

public sealed class TaskbarWatchdogRuntime : ITaskbarWatchdogRuntime
{
    private readonly Action _signalReady;
    private readonly Func<bool> _stopRequested;
    private readonly Func<bool> _ownerExited;
    private readonly TimeSpan _pollInterval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public TaskbarWatchdogRuntime(
        Action signalReady,
        Func<bool> stopRequested,
        Func<bool> ownerExited,
        TimeSpan pollInterval)
        : this(
            signalReady,
            stopRequested,
            ownerExited,
            pollInterval,
            static (delay, cancellationToken) =>
                Task.Delay(delay, cancellationToken))
    {
    }

    internal TaskbarWatchdogRuntime(
        Action signalReady,
        Func<bool> stopRequested,
        Func<bool> ownerExited,
        TimeSpan pollInterval,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        ArgumentNullException.ThrowIfNull(signalReady);
        ArgumentNullException.ThrowIfNull(stopRequested);
        ArgumentNullException.ThrowIfNull(ownerExited);
        ArgumentNullException.ThrowIfNull(delay);
        if (pollInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));

        _signalReady = signalReady;
        _stopRequested = stopRequested;
        _ownerExited = ownerExited;
        _pollInterval = pollInterval;
        _delay = delay;
    }

    public void SignalReady()
        => _signalReady();

    public async Task<TaskbarWatchdogSignal> WaitForStopOrOwnerExitAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_stopRequested())
                return TaskbarWatchdogSignal.Stop;
            if (_ownerExited())
                return TaskbarWatchdogSignal.OwnerExited;

            await _delay(_pollInterval, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}

public sealed class TaskbarWatchdogRunner
{
    public const int RecoveryFailureExitCode = 3;
    public const int RuntimeFailureExitCode = 2;

    private readonly ITaskbarRecoveryService _recoveryService;

    public TaskbarWatchdogRunner(ITaskbarRecoveryService recoveryService)
    {
        _recoveryService = recoveryService
            ?? throw new ArgumentNullException(nameof(recoveryService));
    }

    public async Task<int> RunAsync(
        ITaskbarWatchdogRuntime runtime,
        string expectedLeaseId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        try
        {
            runtime.SignalReady();
            var signal = await runtime
                .WaitForStopOrOwnerExitAsync(cancellationToken)
                .ConfigureAwait(false);
            if (signal == TaskbarWatchdogSignal.Stop)
                return 0;
            if (signal != TaskbarWatchdogSignal.OwnerExited)
                return RuntimeFailureExitCode;

            try
            {
                var recovery = await _recoveryService
                    .TryRecoverAsync(expectedLeaseId, cancellationToken)
                    .ConfigureAwait(false);
                return recovery.Succeeded ? 0 : RecoveryFailureExitCode;
            }
            catch (OperationCanceledException)
            {
                return RuntimeFailureExitCode;
            }
            catch
            {
                return RecoveryFailureExitCode;
            }
        }
        catch (OperationCanceledException)
        {
            return RuntimeFailureExitCode;
        }
        catch
        {
            return RuntimeFailureExitCode;
        }
    }
}
