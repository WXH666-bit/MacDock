namespace MacDock.Core.Services.Taskbar;

public interface ITaskbarLeaseLock
{
    Task<IAsyncDisposable?> TryAcquireAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
