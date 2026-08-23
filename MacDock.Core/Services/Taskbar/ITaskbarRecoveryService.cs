namespace MacDock.Core.Services.Taskbar;

public sealed record TaskbarRecoveryResult(
    bool Succeeded,
    int RestoredCount,
    IReadOnlyList<long> FailedHandles,
    string? Error);

public interface ITaskbarRecoveryService
{
    Task<TaskbarRecoveryResult> TryRecoverAsync(
        string expectedLeaseId,
        CancellationToken cancellationToken = default);

    Task<TaskbarRecoveryResult> TryRecoverStaleAsync(
        CancellationToken cancellationToken = default);
}
