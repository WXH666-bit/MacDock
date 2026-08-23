using MacDock.Core.Services.Taskbar;
using Xunit;

namespace MacDock.Tests;

#pragma warning disable xUnit1030
public sealed class TaskbarWatchdogRunnerTests
{
    [Fact]
    public async Task Runner_WhenOwnerExits_RecoversExpectedLease()
    {
        var events = new List<string>();
        var runtime = FakeWatchdogRuntime.OwnerExitsAfterReady(events);
        var recovery = new FakeTaskbarRecoveryService(events, succeeds: true);

        var exitCode = await new TaskbarWatchdogRunner(recovery)
                .RunAsync(
                    runtime,
                    WatchdogSamples.LeaseId,
                    CancellationToken.None)
            .ConfigureAwait(false);

        Assert.Equal(0, exitCode);
        Assert.Equal(
            [
                "ready",
                $"recover:{WatchdogSamples.LeaseId}",
            ],
            events);
        Assert.Equal(1, runtime.ReadyCalls);
        Assert.Equal(1, runtime.WaitCalls);
        Assert.True(runtime.WaitObservedAfterReady);
        Assert.Equal(1, recovery.Calls);
    }

    [Fact]
    public async Task Runner_WhenStopArrives_DoesNotRecover()
    {
        var events = new List<string>();
        var runtime = FakeWatchdogRuntime.StopAfterReady(events);
        var recovery = new FakeTaskbarRecoveryService(events, succeeds: true);

        var exitCode = await new TaskbarWatchdogRunner(recovery)
                .RunAsync(runtime, WatchdogSamples.LeaseId, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.Equal(0, exitCode);
        Assert.Equal(["ready"], events);
        Assert.Equal(1, runtime.ReadyCalls);
        Assert.Equal(1, runtime.WaitCalls);
        Assert.True(runtime.WaitObservedAfterReady);
        Assert.Equal(0, recovery.Calls);
    }

    [Fact]
    public async Task Runner_WhenStopAndOwnerExitAreSimultaneous_StopWins()
    {
        var events = new List<string>();
        var runtime = FakeWatchdogRuntime.StopAndOwnerExit(events);
        var recovery = new FakeTaskbarRecoveryService(events, succeeds: true);

        var exitCode = await new TaskbarWatchdogRunner(recovery)
                .RunAsync(runtime, WatchdogSamples.LeaseId, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.Equal(0, exitCode);
        Assert.Equal(["ready"], events);
        Assert.Equal(1, runtime.ReadyCalls);
        Assert.Equal(1, runtime.WaitCalls);
        Assert.True(runtime.WaitObservedAfterReady);
        Assert.Equal(0, recovery.Calls);
    }

    [Fact]
    public async Task Runner_WhenRecoveryFails_ReturnsDocumentedRecoveryFailureCode()
    {
        var events = new List<string>();
        var runtime = FakeWatchdogRuntime.OwnerExitsAfterReady(events);
        var recovery = new FakeTaskbarRecoveryService(events, succeeds: false);

        var exitCode = await new TaskbarWatchdogRunner(recovery)
                .RunAsync(runtime, WatchdogSamples.LeaseId, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.Equal(TaskbarWatchdogRunner.RecoveryFailureExitCode, exitCode);
        Assert.Equal(1, runtime.ReadyCalls);
        Assert.Equal(1, runtime.WaitCalls);
        Assert.Equal(1, recovery.Calls);
    }

    [Fact]
    public async Task Runner_PassesWrongLeaseToRecoveryWhichRejectsItOnce()
    {
        var events = new List<string>();
        var runtime = FakeWatchdogRuntime.OwnerExitsAfterReady(events);
        var recovery = new FakeTaskbarRecoveryService(events, succeeds: true)
        {
            RequiredLeaseId = "22222222-2222-2222-2222-222222222222",
        };

        var exitCode = await new TaskbarWatchdogRunner(recovery)
                .RunAsync(runtime, WatchdogSamples.LeaseId, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.Equal(TaskbarWatchdogRunner.RecoveryFailureExitCode, exitCode);
        Assert.Equal(1, recovery.Calls);
        Assert.Equal(WatchdogSamples.LeaseId, recovery.LastLeaseId);
        Assert.Equal(1, runtime.ReadyCalls);
        Assert.Equal(1, runtime.WaitCalls);
    }

    [Fact]
    public async Task Runner_WhenRuntimeThrows_ReturnsNonzeroWithoutRecovery()
    {
        var events = new List<string>();
        var runtime = new FakeWatchdogRuntime(
            TaskbarWatchdogSignal.OwnerExited,
            events)
        {
            WaitException = new IOException("fake runtime failure"),
        };
        var recovery = new FakeTaskbarRecoveryService(events, succeeds: true);

        var exitCode = await new TaskbarWatchdogRunner(recovery)
                .RunAsync(runtime, WatchdogSamples.LeaseId, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.NotEqual(0, exitCode);
        Assert.Equal(1, runtime.ReadyCalls);
        Assert.Equal(1, runtime.WaitCalls);
        Assert.Equal(0, recovery.Calls);
    }

    [Fact]
    public async Task Runner_WhenCanceled_ReturnsNonzeroWithoutRecovery()
    {
        var events = new List<string>();
        var runtime = new FakeWatchdogRuntime(
            TaskbarWatchdogSignal.OwnerExited,
            events)
        {
            WaitException = new OperationCanceledException(),
        };
        var recovery = new FakeTaskbarRecoveryService(events, succeeds: true);

        var exitCode = await new TaskbarWatchdogRunner(recovery)
                .RunAsync(runtime, WatchdogSamples.LeaseId, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.NotEqual(0, exitCode);
        Assert.Equal(1, runtime.ReadyCalls);
        Assert.Equal(1, runtime.WaitCalls);
        Assert.Equal(0, recovery.Calls);
    }

    [Fact]
    public async Task Runner_WhenRecoveryThrows_ReturnsNonzeroAfterOneRecoveryAttempt()
    {
        var events = new List<string>();
        var runtime = FakeWatchdogRuntime.OwnerExitsAfterReady(events);
        var recovery = new FakeTaskbarRecoveryService(events, succeeds: true)
        {
            RecoverException = new IOException("fake recovery exception"),
        };

        var exitCode = await new TaskbarWatchdogRunner(recovery)
                .RunAsync(runtime, WatchdogSamples.LeaseId, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.Equal(TaskbarWatchdogRunner.RecoveryFailureExitCode, exitCode);
        Assert.Equal(1, recovery.Calls);
        Assert.Equal(1, runtime.ReadyCalls);
        Assert.Equal(1, runtime.WaitCalls);
    }

    [Fact]
    public async Task Runner_WhenRecoveryIsCanceled_ReturnsNonzeroAfterOneRecoveryAttempt()
    {
        var events = new List<string>();
        var runtime = FakeWatchdogRuntime.OwnerExitsAfterReady(events);
        var recovery = new FakeTaskbarRecoveryService(events, succeeds: true)
        {
            RecoverException = new OperationCanceledException(),
        };

        var exitCode = await new TaskbarWatchdogRunner(recovery)
                .RunAsync(runtime, WatchdogSamples.LeaseId, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.NotEqual(0, exitCode);
        Assert.Equal(1, recovery.Calls);
        Assert.Equal(1, runtime.ReadyCalls);
        Assert.Equal(1, runtime.WaitCalls);
    }

    [Fact]
    public async Task ConcreteRuntime_WhenBothConditionsAreSignaled_StopWins()
    {
        var events = new List<string>();
        var runtime = new TaskbarWatchdogRuntime(
            () => events.Add("ready"),
            stopRequested: static () => true,
            ownerExited: static () => true,
            pollInterval: TimeSpan.FromMilliseconds(1));
        var recovery = new FakeTaskbarRecoveryService(events, succeeds: true);

        var exitCode = await new TaskbarWatchdogRunner(recovery)
                .RunAsync(runtime, WatchdogSamples.LeaseId, CancellationToken.None)
            .ConfigureAwait(false);

        Assert.Equal(0, exitCode);
        Assert.Equal(["ready"], events);
        Assert.Equal(0, recovery.Calls);
    }

    [Fact]
    public async Task ConcreteRuntime_WhenNeitherConditionIsSignaled_UsesOneBoundedDelayBeforeStop()
    {
        var shouldStop = false;
        var delay = new FakeWatchdogDelay();
        var runtime = new TaskbarWatchdogRuntime(
            static () => { },
            stopRequested: () => shouldStop,
            ownerExited: static () => false,
            pollInterval: TimeSpan.FromMilliseconds(250),
            delay.DelayAsync);

        var waitTask = runtime.WaitForStopOrOwnerExitAsync(CancellationToken.None);
        await delay.Entered.Task.ConfigureAwait(false);

        Assert.False(waitTask.IsCompleted);
        Assert.Equal(1, delay.Calls);
        Assert.Equal(TimeSpan.FromMilliseconds(250), delay.LastDelay);

        shouldStop = true;
        delay.Release();

        Assert.Equal(
            TaskbarWatchdogSignal.Stop,
            await waitTask.ConfigureAwait(false));
        Assert.Equal(1, delay.Calls);
    }

    [Fact]
    public async Task Runner_WhenRuntimeIsCanceledBeforePolling_ReturnsNonzeroWithoutRecovery()
    {
        var delay = new FakeWatchdogDelay();
        var runtime = new TaskbarWatchdogRuntime(
            static () => { },
            stopRequested: static () => false,
            ownerExited: static () => false,
            pollInterval: TimeSpan.FromSeconds(1),
            delay.DelayAsync);
        var recovery = new FakeTaskbarRecoveryService([], succeeds: true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exitCode = await new TaskbarWatchdogRunner(recovery)
                .RunAsync(runtime, WatchdogSamples.LeaseId, cancellation.Token)
            .ConfigureAwait(false);

        Assert.Equal(TaskbarWatchdogRunner.RuntimeFailureExitCode, exitCode);
        Assert.Equal(0, delay.Calls);
        Assert.Equal(0, recovery.Calls);
    }

    [Fact]
    public async Task Runner_WhenRuntimeIsCanceledDuringPollingDelay_ReturnsNonzeroWithoutRecovery()
    {
        var delay = new FakeWatchdogDelay();
        var runtime = new TaskbarWatchdogRuntime(
            static () => { },
            stopRequested: static () => false,
            ownerExited: static () => false,
            pollInterval: TimeSpan.FromSeconds(1),
            delay.DelayAsync);
        var recovery = new FakeTaskbarRecoveryService([], succeeds: true);
        using var cancellation = new CancellationTokenSource();

        var runTask = new TaskbarWatchdogRunner(recovery)
            .RunAsync(runtime, WatchdogSamples.LeaseId, cancellation.Token);
        await delay.Entered.Task.ConfigureAwait(false);

        cancellation.Cancel();

        var exitCode = await runTask.ConfigureAwait(false);

        Assert.Equal(TaskbarWatchdogRunner.RuntimeFailureExitCode, exitCode);
        Assert.Equal(1, delay.Calls);
        Assert.Equal(0, recovery.Calls);
    }
}
