namespace MacDock.Core.Services.Taskbar;

public sealed record TaskbarRecoveryGuardRequest(
    string LeaseId,
    int OwnerProcessId,
    long OwnerProcessStartTimeUtcTicks,
    string JournalPath);

public sealed record TaskbarRecoveryGuardSession(int WatchdogProcessId);

public interface ITaskbarRecoveryGuard : IAsyncDisposable
{
    Task<TaskbarRecoveryGuardSession?> ArmAsync(
        TaskbarRecoveryGuardRequest request,
        TimeSpan readyTimeout,
        CancellationToken cancellationToken);

    Task DisarmAsync(
        string leaseId,
        TimeSpan exitTimeout,
        CancellationToken cancellationToken);
}
