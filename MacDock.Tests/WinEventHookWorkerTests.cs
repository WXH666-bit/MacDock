using MacDock.Core.Services;
using Xunit;

namespace MacDock.Tests;

public sealed class WinEventHookWorkerTests
{
    [Fact]
    public void Dispose_RunsInitializationAndCleanupOnTheSameDedicatedThread()
    {
        using var initialized = new ManualResetEventSlim(false);
        var callerThreadId = Environment.CurrentManagedThreadId;
        var initializeThreadId = 0;
        var cleanupThreadId = 0;
        var errors = new List<Exception>();

        var worker = new WinEventHookWorker(
            () =>
            {
                initializeThreadId = Environment.CurrentManagedThreadId;
                initialized.Set();
            },
            () => cleanupThreadId = Environment.CurrentManagedThreadId,
            errors.Add);

        Assert.True(initialized.Wait(TimeSpan.FromSeconds(2)));
        worker.Dispose();

        Assert.NotEqual(callerThreadId, initializeThreadId);
        Assert.Equal(initializeThreadId, cleanupThreadId);
        Assert.Empty(errors);
    }
}
