using MacDock.Core.Services.Taskbar;
using Xunit;

namespace MacDock.Tests;

public sealed class TaskbarLeaseFileLockTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(), $"macdock-task2-lock-{Guid.NewGuid():N}");

    public TaskbarLeaseFileLockTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task Acquire_WhenUncontended_CreatesParentAndLeavesLockFileAfterRelease()
    {
        var path = Path.Combine(_tempDirectory, "nested", "taskbar-lease.lock");
        var fileLock = new TaskbarLeaseFileLock(path);

        var handle = await fileLock.TryAcquireAsync(TimeSpan.Zero);

        Assert.NotNull(handle);
        await handle!.DisposeAsync();
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Acquire_WhenAlreadyHeld_ReturnsNullAfterContentionTimeout()
    {
        var path = Path.Combine(_tempDirectory, "taskbar-lease.lock");
        var fileLock = new TaskbarLeaseFileLock(path);
        var first = await fileLock.TryAcquireAsync(TimeSpan.Zero);
        Assert.NotNull(first);

        try
        {
            var second = await fileLock.TryAcquireAsync(TimeSpan.FromMilliseconds(100));

            Assert.Null(second);
        }
        finally
        {
            await first!.DisposeAsync();
        }
    }

    [Fact]
    public async Task Acquire_WithZeroTimeoutStillAttemptsImmediately()
    {
        var path = Path.Combine(_tempDirectory, "existing-directory");
        Directory.CreateDirectory(path);
        var fileLock = new TaskbarLeaseFileLock(path);

        var exception = await Record.ExceptionAsync(async () =>
        {
            await fileLock.TryAcquireAsync(TimeSpan.Zero);
        });

        Assert.NotNull(exception);
        Assert.IsNotType<FileNotFoundException>(exception);
        Assert.IsNotType<DirectoryNotFoundException>(exception);
    }

    [Fact]
    public async Task Acquire_WhenDeadlinePassesAfterContention_DoesNotOpenAgain()
    {
        var path = Path.Combine(_tempDirectory, "taskbar-lease.lock");
        var fileLock = new TaskbarLeaseFileLock(path);
        var first = await fileLock.TryAcquireAsync(TimeSpan.Zero);
        Assert.NotNull(first);
        var elapsed = TimeSpan.Zero;

        var contender = new TaskbarLeaseFileLock(
            path,
            _ => elapsed,
            (_, _) =>
            {
                elapsed = TimeSpan.FromMilliseconds(101);
                return first!.DisposeAsync().AsTask();
            });

        IAsyncDisposable? acquired = null;
        try
        {
            acquired = await contender.TryAcquireAsync(TimeSpan.FromMilliseconds(100));

            Assert.Null(acquired);
        }
        finally
        {
            if (acquired is not null)
                await acquired.DisposeAsync();
        }
    }

    [Fact]
    public async Task Acquire_AfterRelease_AllowsNextHolder()
    {
        var path = Path.Combine(_tempDirectory, "taskbar-lease.lock");
        var fileLock = new TaskbarLeaseFileLock(path);
        var first = await fileLock.TryAcquireAsync(TimeSpan.Zero);
        Assert.NotNull(first);

        await first!.DisposeAsync();
        var second = await fileLock.TryAcquireAsync(TimeSpan.Zero);

        Assert.NotNull(second);
        await second!.DisposeAsync();
    }

    [Fact]
    public async Task Acquire_WhenCancelledDuringContention_ThrowsAndDoesNotOwnLock()
    {
        var path = Path.Combine(_tempDirectory, "taskbar-lease.lock");
        var fileLock = new TaskbarLeaseFileLock(path);
        var first = await fileLock.TryAcquireAsync(TimeSpan.Zero);
        Assert.NotNull(first);
        using var cancellation = new CancellationTokenSource();

        try
        {
            cancellation.CancelAfter(100);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => fileLock.TryAcquireAsync(Timeout.InfiniteTimeSpan, cancellation.Token));
        }
        finally
        {
            await first!.DisposeAsync();
        }

        var afterCancellation = await fileLock.TryAcquireAsync(TimeSpan.Zero);
        Assert.NotNull(afterCancellation);
        await afterCancellation!.DisposeAsync();
    }

    [Fact]
    public async Task Acquire_WhenAlreadyCancelled_ThrowsWithoutTakingLock()
    {
        var path = Path.Combine(_tempDirectory, "taskbar-lease.lock");
        var fileLock = new TaskbarLeaseFileLock(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fileLock.TryAcquireAsync(TimeSpan.Zero, cancellation.Token));

        var afterCancellation = await fileLock.TryAcquireAsync(TimeSpan.Zero);
        Assert.NotNull(afterCancellation);
        await afterCancellation!.DisposeAsync();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }
}
