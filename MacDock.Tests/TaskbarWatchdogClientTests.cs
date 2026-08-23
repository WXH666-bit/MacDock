using MacDock.Core.Services.Taskbar;
using Xunit;

namespace MacDock.Tests;

#pragma warning disable xUnit1030
public sealed class TaskbarWatchdogClientTests
{
    [Fact]
    public async Task Arm_WhenReadySucceeds_ReturnsSessionAndUsesExactArguments()
    {
        var process = new FakeWatchdogProcess { Id = 4321 };
        var launcher = new FakeWatchdogProcessLauncher(process);
        var harness = WatchdogClientHarness.Create(launcher);
        var callerThreadId = Environment.CurrentManagedThreadId;

        try
        {
            var session = await harness.Client.ArmAsync(
                    WatchdogSamples.Request(),
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None)
                .ConfigureAwait(false);

            Assert.NotNull(session);
            Assert.Equal(process.Id, session!.WatchdogProcessId);
            Assert.Equal(harness.HelperPath, launcher.LastPath);
            Assert.Equal(12, launcher.LastArguments!.Count);
            Assert.Equal("--parent-pid", launcher.LastArguments[0]);
            Assert.Equal("1234", launcher.LastArguments[1]);
            Assert.Equal("--parent-start-ticks", launcher.LastArguments[2]);
            Assert.Equal("638000000000000000", launcher.LastArguments[3]);
            Assert.Equal("--lease-id", launcher.LastArguments[4]);
            Assert.Equal(WatchdogSamples.LeaseId, launcher.LastArguments[5]);
            Assert.Equal("--journal", launcher.LastArguments[6]);
            Assert.Equal(WatchdogSamples.JournalPath, launcher.LastArguments[7]);
            Assert.Equal("--ready-event", launcher.LastArguments[8]);
            Assert.Matches(
                @"^Local\\MacDock\.Taskbar\.[0-9a-f]{32}\.ready$",
                launcher.LastArguments[9]);
            Assert.Equal("--stop-event", launcher.LastArguments[10]);
            Assert.Matches(
                @"^Local\\MacDock\.Taskbar\.[0-9a-f]{32}\.stop$",
                launcher.LastArguments[11]);
            Assert.NotEqual(launcher.LastArguments[9], launcher.LastArguments[11]);
            Assert.Contains(
                process.HasExitedThreadIds,
                threadId => threadId != callerThreadId);
        }
        finally
        {
            await harness.Client.DisposeAsync().ConfigureAwait(false);
            launcher.DisposeObservers();
        }
    }

    [Fact]
    public async Task Arm_WhenLauncherThrows_ReturnsNullWithoutAChildCleanup()
    {
        var launcher = new FakeWatchdogProcessLauncher
        {
            StartException = new InvalidOperationException("fake launch failure"),
        };
        var harness = WatchdogClientHarness.Create(launcher);

        try
        {
            var session = await harness.Client.ArmAsync(
                    WatchdogSamples.Request(),
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None)
                .ConfigureAwait(false);

            Assert.Null(session);
            Assert.Equal(1, launcher.StartCalls);
        }
        finally
        {
            await harness.Client.DisposeAsync().ConfigureAwait(false);
            launcher.DisposeObservers();
        }
    }

    [Fact]
    public async Task Arm_WhenChildIdIsInvalid_TerminatesReturnedChildAndReturnsNull()
    {
        var process = new FakeWatchdogProcess { Id = 0 };
        var launcher = new FakeWatchdogProcessLauncher(process);
        var harness = WatchdogClientHarness.Create(launcher);

        try
        {
            var session = await harness.Client.ArmAsync(
                    WatchdogSamples.Request(),
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None)
                .ConfigureAwait(false);

            Assert.Null(session);
            Assert.True(process.Terminated);
            Assert.True(process.Disposed);
        }
        finally
        {
            await harness.Client.DisposeAsync().ConfigureAwait(false);
            launcher.DisposeObservers();
        }
    }

    [Fact]
    public async Task Arm_WhenChildExitsBeforeReady_TerminatesChildAndReturnsNull()
    {
        var process = new FakeWatchdogProcess { HasExited = true };
        var launcher = new FakeWatchdogProcessLauncher(process)
        {
            SetReadyEvent = false,
        };
        var harness = WatchdogClientHarness.Create(launcher);

        try
        {
            var session = await harness.Client.ArmAsync(
                    WatchdogSamples.Request(),
                    TimeSpan.FromMilliseconds(100),
                    CancellationToken.None)
                .ConfigureAwait(false);

            Assert.Null(session);
            Assert.True(process.Terminated);
            Assert.True(process.Disposed);
        }
        finally
        {
            await harness.Client.DisposeAsync().ConfigureAwait(false);
            launcher.DisposeObservers();
        }
    }

    [Fact]
    public async Task Arm_WhenReadyTimesOut_TerminatesChild()
    {
        var process = new FakeWatchdogProcess { SignalsReady = false };
        var launcher = new FakeWatchdogProcessLauncher(process);
        var harness = WatchdogClientHarness.Create(launcher);

        try
        {
            var session = await harness.Client.ArmAsync(
                    WatchdogSamples.Request(),
                    TimeSpan.FromMilliseconds(40),
                    CancellationToken.None)
                .ConfigureAwait(false);

            Assert.Null(session);
            Assert.True(process.Terminated);
            Assert.True(process.Disposed);
        }
        finally
        {
            await harness.Client.DisposeAsync().ConfigureAwait(false);
            launcher.DisposeObservers();
        }
    }

    [Fact]
    public async Task Arm_WhenCanceledBeforeStart_DoesNotLaunchChild()
    {
        var launcher = new FakeWatchdogProcessLauncher(new FakeWatchdogProcess());
        var harness = WatchdogClientHarness.Create(launcher);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            var session = await harness.Client.ArmAsync(
                    WatchdogSamples.Request(),
                    TimeSpan.FromSeconds(1),
                    cancellation.Token)
                .ConfigureAwait(false);

            Assert.Null(session);
            Assert.Equal(0, launcher.StartCalls);
        }
        finally
        {
            await harness.Client.DisposeAsync().ConfigureAwait(false);
            launcher.DisposeObservers();
        }
    }

    [Fact]
    public async Task Arm_WhenCanceledWhileWaiting_TerminatesChild()
    {
        var process = new FakeWatchdogProcess { SignalsReady = false };
        var launcher = new FakeWatchdogProcessLauncher(process);
        var harness = WatchdogClientHarness.Create(launcher);
        using var cancellation = new CancellationTokenSource();

        try
        {
            var armTask = harness.Client.ArmAsync(
                WatchdogSamples.Request(),
                TimeSpan.FromSeconds(5),
                cancellation.Token);
            await launcher.StartEntered.Task.ConfigureAwait(false);
            cancellation.Cancel();

            Assert.Null(await armTask.ConfigureAwait(false));
            Assert.True(process.Terminated);
            Assert.True(process.Disposed);
        }
        finally
        {
            await harness.Client.DisposeAsync().ConfigureAwait(false);
            launcher.DisposeObservers();
        }
    }

    [Fact]
    public async Task Arm_WhenChildExitsAroundReady_ReturnsNull()
    {
        var process = new FakeWatchdogProcess();
        var launcher = new FakeWatchdogProcessLauncher(process)
        {
            ExitAfterReady = true,
        };
        var harness = WatchdogClientHarness.Create(launcher);

        try
        {
            var session = await harness.Client.ArmAsync(
                    WatchdogSamples.Request(),
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None)
                .ConfigureAwait(false);

            Assert.Null(session);
            Assert.True(process.Terminated);
            Assert.True(process.Disposed);
        }
        finally
        {
            await harness.Client.DisposeAsync().ConfigureAwait(false);
            launcher.DisposeObservers();
        }
    }

    [Fact]
    public async Task Arm_WhenAlreadyActive_RejectsConcurrentArms()
    {
        var process = new FakeWatchdogProcess { Id = 4321 };
        var launcher = new FakeWatchdogProcessLauncher(process);
        var harness = WatchdogClientHarness.Create(launcher);

        try
        {
            Assert.NotNull(
                await harness.Client.ArmAsync(
                        WatchdogSamples.Request(),
                        TimeSpan.FromSeconds(1),
                        CancellationToken.None)
                    .ConfigureAwait(false));

            var results = await Task.WhenAll(
                    harness.Client.ArmAsync(
                        WatchdogSamples.Request(),
                        TimeSpan.FromSeconds(1),
                        CancellationToken.None),
                    harness.Client.ArmAsync(
                        WatchdogSamples.Request(),
                        TimeSpan.FromSeconds(1),
                        CancellationToken.None))
                .ConfigureAwait(false);

            Assert.All(results, result => Assert.Null(result));
            Assert.Equal(1, launcher.StartCalls);
        }
        finally
        {
            await harness.Client.DisposeAsync().ConfigureAwait(false);
            launcher.DisposeObservers();
        }
    }

    [Fact]
    public async Task Arm_ConcurrentInitialCallsSerializeBeforeSecondLaunch()
    {
        var process = new FakeWatchdogProcess { Id = 4321 };
        var launcher = new FakeWatchdogProcessLauncher(process)
        {
            BlockBeforeReady = true,
        };
        var harness = WatchdogClientHarness.Create(launcher);

        try
        {
            var firstArm = Task.Run(() => harness.Client.ArmAsync(
                WatchdogSamples.Request(),
                TimeSpan.FromSeconds(1),
                CancellationToken.None));
            await launcher.ReadyEntered.Task.ConfigureAwait(false);

            var secondArm = harness.Client.ArmAsync(
                WatchdogSamples.Request(),
                TimeSpan.FromSeconds(1),
                CancellationToken.None);

            Assert.False(secondArm.IsCompleted);
            Assert.Equal(1, launcher.StartCalls);

            launcher.AllowReady.TrySetResult(true);
            var sessions = await Task.WhenAll(firstArm, secondArm).ConfigureAwait(false);

            Assert.Single(sessions, session => session is not null);
            Assert.Equal(1, launcher.StartCalls);
        }
        finally
        {
            await harness.Client.DisposeAsync().ConfigureAwait(false);
            launcher.DisposeObservers();
        }
    }

    [Fact]
    public async Task LauncherCallbackCanReenterClientWithoutDeadlock()
    {
        var process = new FakeWatchdogProcess { Id = 4321 };
        var launcher = new FakeWatchdogProcessLauncher(process);
        var harness = WatchdogClientHarness.Create(launcher);
        Task<TaskbarRecoveryGuardSession?>? reentrantArm = null;
        var callbackReturned = false;
        launcher.OnStart = () =>
        {
            reentrantArm = harness.Client.ArmAsync(
                WatchdogSamples.Request(),
                TimeSpan.FromSeconds(1),
                CancellationToken.None);
            callbackReturned = true;
        };

        try
        {
            var primarySession = await harness.Client.ArmAsync(
                    WatchdogSamples.Request(),
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None)
                .ConfigureAwait(false);

            Assert.NotNull(primarySession);
            Assert.True(callbackReturned);
            Assert.NotNull(reentrantArm);
            Assert.Null(await reentrantArm!.ConfigureAwait(false));
            Assert.Equal(1, launcher.StartCalls);
        }
        finally
        {
            await harness.Client.DisposeAsync().ConfigureAwait(false);
            launcher.DisposeObservers();
        }
    }

    [Fact]
    public async Task Arm_WhenCancellationWinsAfterReadyObservation_FailsClosed()
    {
        var process = new FakeWatchdogProcess
        {
            BlockOnSecondHasExitedRead = true,
        };
        var launcher = new FakeWatchdogProcessLauncher(process);
        var harness = WatchdogClientHarness.Create(launcher);
        using var cancellation = new CancellationTokenSource();

        try
        {
            var armTask = harness.Client.ArmAsync(
                WatchdogSamples.Request(),
                TimeSpan.FromSeconds(1),
                cancellation.Token);
            await process.SecondHasExitedReadEntered.Task.ConfigureAwait(false);

            cancellation.Cancel();
            process.AllowSecondHasExitedRead.TrySetResult(true);

            Assert.Null(await armTask.ConfigureAwait(false));
            Assert.True(process.Terminated);
            Assert.True(process.Disposed);
        }
        finally
        {
            process.AllowSecondHasExitedRead.TrySetResult(true);
            await harness.Client.DisposeAsync().ConfigureAwait(false);
            launcher.DisposeObservers();
        }
    }

    [Fact]
    public async Task Arm_WhenChildExitsDuringReadyRecheck_FailsClosed()
    {
        var process = new FakeWatchdogProcess();
        process.OnHasExitedRead = readCall =>
        {
            if (readCall == 2)
                process.HasExited = true;
        };
        var launcher = new FakeWatchdogProcessLauncher(process);
        var harness = WatchdogClientHarness.Create(launcher);

        try
        {
            var session = await harness.Client.ArmAsync(
                    WatchdogSamples.Request(),
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None)
                .ConfigureAwait(false);

            Assert.Null(session);
            Assert.True(process.Terminated);
            Assert.True(process.Disposed);
        }
        finally
        {
            await harness.Client.DisposeAsync().ConfigureAwait(false);
            launcher.DisposeObservers();
        }
    }

    [Fact]
    public async Task Disarm_WithWrongLeaseDoesNotSignalOrTerminate()
    {
        var process = new FakeWatchdogProcess { Id = 4321 };
        var launcher = new FakeWatchdogProcessLauncher(process);
        var harness = WatchdogClientHarness.Create(launcher);

        try
        {
            Assert.NotNull(
                await harness.Client.ArmAsync(
                        WatchdogSamples.Request(),
                        TimeSpan.FromSeconds(1),
                        CancellationToken.None)
                    .ConfigureAwait(false));

            await harness.Client.DisarmAsync(
                    "22222222-2222-2222-2222-222222222222",
                    TimeSpan.FromMilliseconds(100),
                    CancellationToken.None)
                .ConfigureAwait(false);

            Assert.False(launcher.StopObserver!.WaitOne(0));
            Assert.False(process.Terminated);
            Assert.Equal(0, process.WaitForExitCalls);
        }
        finally
        {
            await harness.Client.DisposeAsync().ConfigureAwait(false);
            launcher.DisposeObservers();
        }
    }

    [Fact]
    public async Task Disarm_WithExactLeaseSignalsAndWaitsForCleanExit()
    {
        var process = new FakeWatchdogProcess { Id = 4321 };
        var launcher = new FakeWatchdogProcessLauncher(process);
        var harness = WatchdogClientHarness.Create(launcher);
        var callerThreadId = Environment.CurrentManagedThreadId;

        try
        {
            Assert.NotNull(
                await harness.Client.ArmAsync(
                        WatchdogSamples.Request(),
                        TimeSpan.FromSeconds(1),
                        CancellationToken.None)
                    .ConfigureAwait(false));

            await harness.Client.DisarmAsync(
                    WatchdogSamples.LeaseId,
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None)
                .ConfigureAwait(false);

            Assert.True(launcher.StopObserver!.WaitOne(0));
            Assert.True(launcher.StopObserver.WaitOne(0));
            Assert.Equal(1, process.WaitForExitCalls);
            Assert.NotEqual(callerThreadId, process.WaitForExitThreadId);
            Assert.False(process.Terminated);
            Assert.True(process.Disposed);
        }
        finally
        {
            await harness.Client.DisposeAsync().ConfigureAwait(false);
            launcher.DisposeObservers();
        }
    }

    [Fact]
    public async Task Disarm_WhenStopEventSetThrows_PropagatesAndRetainsSessionForRetry()
    {
        var firstProcess = new FakeWatchdogProcess { Id = 4321 };
        var secondProcess = new FakeWatchdogProcess { Id = 4322 };
        var launcher = new FakeWatchdogProcessLauncher(firstProcess)
        {
            ProcessFactory = new Queue<FakeWatchdogProcess>([firstProcess, secondProcess]).Dequeue,
        };
        var eventFactory = new FakeWatchdogEventFactory
        {
            StopSetException = new IOException("fake stop event failure"),
        };
        var harness = WatchdogClientHarness.Create(launcher, eventFactory.Create);

        try
        {
            Assert.NotNull(
                await harness.Client.ArmAsync(
                        WatchdogSamples.Request(),
                        TimeSpan.FromSeconds(1),
                        CancellationToken.None)
                    .ConfigureAwait(false));

            var exception = await Assert.ThrowsAsync<IOException>(
                    () => harness.Client.DisarmAsync(
                        WatchdogSamples.LeaseId,
                        TimeSpan.FromSeconds(1),
                        CancellationToken.None))
                .ConfigureAwait(false);

            Assert.Equal("fake stop event failure", exception.Message);
            Assert.Equal(1, launcher.StartCalls);
            Assert.Equal(0, firstProcess.WaitForExitCalls);
            Assert.Equal(0, firstProcess.TerminateCalls);
            Assert.False(firstProcess.Disposed);

            Assert.Null(
                await harness.Client.ArmAsync(
                        WatchdogSamples.Request(),
                        TimeSpan.FromSeconds(1),
                        CancellationToken.None)
                    .ConfigureAwait(false));
            Assert.Equal(1, launcher.StartCalls);

            eventFactory.StopEvent!.SetException = null;
            await harness.Client.DisarmAsync(
                    WatchdogSamples.LeaseId,
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None)
                .ConfigureAwait(false);
            await harness.Client.DisarmAsync(
                    WatchdogSamples.LeaseId,
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None)
                .ConfigureAwait(false);

            var secondSession = await harness.Client.ArmAsync(
                    WatchdogSamples.Request(),
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None)
                .ConfigureAwait(false);

            Assert.NotNull(secondSession);
            Assert.Equal(2, launcher.StartCalls);
            Assert.Equal(1, firstProcess.WaitForExitCalls);
            Assert.True(firstProcess.Disposed);
        }
        finally
        {
            await harness.Client.DisposeAsync().ConfigureAwait(false);
            launcher.DisposeObservers();
        }
    }

    [Fact]
    public async Task Disarm_WhenWaitForExitThrows_PropagatesAndRetainsSessionWithoutTerminating()
    {
        var firstProcess = new FakeWatchdogProcess
        {
            Id = 4321,
            WaitForExitException = new IOException("fake wait failure"),
        };
        var launcher = new FakeWatchdogProcessLauncher(firstProcess);
        var harness = WatchdogClientHarness.Create(launcher);

        try
        {
            Assert.NotNull(
                await harness.Client.ArmAsync(
                        WatchdogSamples.Request(),
                        TimeSpan.FromSeconds(1),
                        CancellationToken.None)
                    .ConfigureAwait(false));

            var exception = await Assert.ThrowsAsync<IOException>(
                    () => harness.Client.DisarmAsync(
                        WatchdogSamples.LeaseId,
                        TimeSpan.FromSeconds(1),
                        CancellationToken.None))
                .ConfigureAwait(false);

            Assert.Equal("fake wait failure", exception.Message);
            Assert.Equal(1, firstProcess.WaitForExitCalls);
            Assert.Equal(0, firstProcess.TerminateCalls);
            Assert.False(firstProcess.Disposed);

            Assert.Null(
                await harness.Client.ArmAsync(
                        WatchdogSamples.Request(),
                        TimeSpan.FromSeconds(1),
                        CancellationToken.None)
                    .ConfigureAwait(false));

            firstProcess.WaitForExitException = null;
            await harness.Client.DisarmAsync(
                    WatchdogSamples.LeaseId,
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None)
                .ConfigureAwait(false);

            Assert.Equal(2, firstProcess.WaitForExitCalls);
            Assert.Equal(0, firstProcess.TerminateCalls);
            Assert.True(firstProcess.Disposed);
        }
        finally
        {
            await harness.Client.DisposeAsync().ConfigureAwait(false);
            launcher.DisposeObservers();
        }
    }

    [Fact]
    public async Task Disarm_WhenTerminationThrows_RetainsSessionForRetry()
    {
        var process = new FakeWatchdogProcess
        {
            Id = 4321,
            WaitForExitResult = false,
            TerminateException = new IOException("fake terminate failure"),
        };
        var launcher = new FakeWatchdogProcessLauncher(process);
        var harness = WatchdogClientHarness.Create(launcher);

        try
        {
            Assert.NotNull(
                await harness.Client.ArmAsync(
                        WatchdogSamples.Request(),
                        TimeSpan.FromSeconds(1),
                        CancellationToken.None)
                    .ConfigureAwait(false));

            await Assert.ThrowsAsync<IOException>(
                    () => harness.Client.DisarmAsync(
                        WatchdogSamples.LeaseId,
                        TimeSpan.FromSeconds(1),
                        CancellationToken.None))
                .ConfigureAwait(false);

            Assert.Equal(1, process.WaitForExitCalls);
            Assert.Equal(1, process.TerminateCalls);
            Assert.False(process.Terminated);
            Assert.False(process.Disposed);

            Assert.Null(
                await harness.Client.ArmAsync(
                        WatchdogSamples.Request(),
                        TimeSpan.FromSeconds(1),
                        CancellationToken.None)
                    .ConfigureAwait(false));

            process.TerminateException = null;
            await harness.Client.DisarmAsync(
                    WatchdogSamples.LeaseId,
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None)
                .ConfigureAwait(false);

            await harness.Client.DisarmAsync(
                    WatchdogSamples.LeaseId,
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None)
                .ConfigureAwait(false);

            Assert.Equal(2, process.WaitForExitCalls);
            Assert.Equal(2, process.TerminateCalls);
            Assert.True(process.Terminated);
            Assert.True(process.Disposed);
        }
        finally
        {
            await harness.Client.DisposeAsync().ConfigureAwait(false);
            launcher.DisposeObservers();
        }
    }

    [Fact]
    public async Task ConcurrentDisarmWaiterRetriesPublishedArmedStateAfterLeaderFailure()
    {
        var process = new FakeWatchdogProcess
        {
            Id = 4321,
            BlockWaitForExit = true,
            WaitForExitExceptions = new Queue<Exception?>([
                new IOException("first wait failure"),
                null,
            ]),
        };
        var launcher = new FakeWatchdogProcessLauncher(process);
        var harness = WatchdogClientHarness.Create(launcher);

        try
        {
            Assert.NotNull(
                await harness.Client.ArmAsync(
                        WatchdogSamples.Request(),
                        TimeSpan.FromSeconds(1),
                        CancellationToken.None)
                    .ConfigureAwait(false));

            var leader = harness.Client.DisarmAsync(
                WatchdogSamples.LeaseId,
                TimeSpan.FromSeconds(1),
                CancellationToken.None);
            await process.WaitForExitEntered.Task.ConfigureAwait(false);

            var waiter = harness.Client.DisarmAsync(
                WatchdogSamples.LeaseId,
                TimeSpan.FromSeconds(1),
                CancellationToken.None);

            process.AllowWaitForExit.TrySetResult(true);

            await Assert.ThrowsAsync<IOException>(() => leader).ConfigureAwait(false);
            await waiter.ConfigureAwait(false);

            Assert.Equal(2, process.WaitForExitCalls);
            Assert.Equal(0, process.TerminateCalls);
            Assert.True(process.Disposed);
        }
        finally
        {
            process.AllowWaitForExit.TrySetResult(true);
            await harness.Client.DisposeAsync().ConfigureAwait(false);
            launcher.DisposeObservers();
        }
    }

    [Fact]
    public async Task Disarm_WaitForExitYieldsCallerUntilWorkerCompletes()
    {
        var process = new FakeWatchdogProcess
        {
            Id = 4321,
            BlockWaitForExit = true,
        };
        var launcher = new FakeWatchdogProcessLauncher(process);
        var harness = WatchdogClientHarness.Create(launcher);
        var callerThreadId = Environment.CurrentManagedThreadId;

        try
        {
            Assert.NotNull(
                await harness.Client.ArmAsync(
                        WatchdogSamples.Request(),
                        TimeSpan.FromSeconds(1),
                        CancellationToken.None)
                    .ConfigureAwait(false));

            var disarmTask = harness.Client.DisarmAsync(
                WatchdogSamples.LeaseId,
                TimeSpan.FromSeconds(1),
                CancellationToken.None);
            await process.WaitForExitEntered.Task.ConfigureAwait(false);

            Assert.False(disarmTask.IsCompleted);
            Assert.NotEqual(callerThreadId, process.WaitForExitThreadId);

            process.AllowWaitForExit.TrySetResult(true);
            await disarmTask.ConfigureAwait(false);
            Assert.False(process.Terminated);
        }
        finally
        {
            process.AllowWaitForExit.TrySetResult(true);
            await harness.Client.DisposeAsync().ConfigureAwait(false);
            launcher.DisposeObservers();
        }
    }

    [Fact]
    public async Task Disarm_CancellationAfterClaimWaitsForTimeoutBeforeTerminating()
    {
        var process = new FakeWatchdogProcess
        {
            Id = 4321,
            BlockWaitForExit = true,
            WaitForExitResult = false,
        };
        var launcher = new FakeWatchdogProcessLauncher(process);
        var harness = WatchdogClientHarness.Create(launcher);
        using var cancellation = new CancellationTokenSource();

        try
        {
            Assert.NotNull(
                await harness.Client.ArmAsync(
                        WatchdogSamples.Request(),
                        TimeSpan.FromSeconds(1),
                        CancellationToken.None)
                    .ConfigureAwait(false));

            var disarmTask = harness.Client.DisarmAsync(
                WatchdogSamples.LeaseId,
                TimeSpan.FromMilliseconds(100),
                cancellation.Token);
            await process.WaitForExitEntered.Task.ConfigureAwait(false);

            cancellation.Cancel();
            Assert.False(process.Terminated);
            process.AllowWaitForExit.TrySetResult(true);
            await disarmTask.ConfigureAwait(false);

            Assert.Equal(1, process.TerminateCalls);
            Assert.True(process.Disposed);
        }
        finally
        {
            process.AllowWaitForExit.TrySetResult(true);
            await harness.Client.DisposeAsync().ConfigureAwait(false);
            launcher.DisposeObservers();
        }
    }

    [Fact]
    public async Task Disarm_WhenExitTimesOutTerminatesOnlyTheChild()
    {
        var process = new FakeWatchdogProcess
        {
            Id = 4321,
            WaitForExitResult = false,
        };
        var launcher = new FakeWatchdogProcessLauncher(process);
        var harness = WatchdogClientHarness.Create(launcher);

        try
        {
            Assert.NotNull(
                await harness.Client.ArmAsync(
                        WatchdogSamples.Request(),
                        TimeSpan.FromSeconds(1),
                        CancellationToken.None)
                    .ConfigureAwait(false));

            await harness.Client.DisarmAsync(
                    WatchdogSamples.LeaseId,
                    TimeSpan.FromMilliseconds(20),
                    CancellationToken.None)
                .ConfigureAwait(false);

            Assert.True(process.Terminated);
            Assert.Equal(1, process.TerminateCalls);
            Assert.True(process.Disposed);
        }
        finally
        {
            await harness.Client.DisposeAsync().ConfigureAwait(false);
            launcher.DisposeObservers();
        }
    }

    [Fact]
    public async Task RepeatedDisarmIsIdempotentAndSuccessfulDisarmPermitsRearm()
    {
        var firstProcess = new FakeWatchdogProcess { Id = 4321 };
        var secondProcess = new FakeWatchdogProcess { Id = 4322 };
        var launcher = new FakeWatchdogProcessLauncher(firstProcess)
        {
            ProcessFactory = new Queue<FakeWatchdogProcess>([firstProcess, secondProcess]).Dequeue,
        };
        var harness = WatchdogClientHarness.Create(launcher);

        try
        {
            Assert.NotNull(
                await harness.Client.ArmAsync(
                        WatchdogSamples.Request(),
                        TimeSpan.FromSeconds(1),
                        CancellationToken.None)
                    .ConfigureAwait(false));
            await harness.Client.DisarmAsync(
                    WatchdogSamples.LeaseId,
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None)
                .ConfigureAwait(false);
            await harness.Client.DisarmAsync(
                    WatchdogSamples.LeaseId,
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None)
                .ConfigureAwait(false);

            var secondSession = await harness.Client.ArmAsync(
                    WatchdogSamples.Request(),
                    TimeSpan.FromSeconds(1),
                    CancellationToken.None)
                .ConfigureAwait(false);

            Assert.NotNull(secondSession);
            Assert.Equal(secondProcess.Id, secondSession!.WatchdogProcessId);
            Assert.Equal(2, launcher.StartCalls);
            Assert.Equal(1, firstProcess.WaitForExitCalls);
            Assert.Equal(2, launcher.ReadyEventNames.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(2, launcher.StopEventNames.Distinct(StringComparer.Ordinal).Count());
            Assert.True(launcher.ReadyObservers[0].WaitOne(0));
            Assert.True(launcher.ReadyObservers[0].WaitOne(0));
        }
        finally
        {
            await harness.Client.DisposeAsync().ConfigureAwait(false);
            launcher.DisposeObservers();
        }
    }

    [Fact]
    public async Task Arm_WaitsForDisarmCleanupBeforeRearming()
    {
        var firstProcess = new FakeWatchdogProcess
        {
            Id = 4321,
            BlockDispose = true,
        };
        var secondProcess = new FakeWatchdogProcess { Id = 4322 };
        var launcher = new FakeWatchdogProcessLauncher(firstProcess)
        {
            ProcessFactory = new Queue<FakeWatchdogProcess>([firstProcess, secondProcess]).Dequeue,
        };
        var harness = WatchdogClientHarness.Create(launcher);

        try
        {
            Assert.NotNull(
                await harness.Client.ArmAsync(
                        WatchdogSamples.Request(),
                        TimeSpan.FromSeconds(1),
                        CancellationToken.None)
                    .ConfigureAwait(false));

            var disarmTask = harness.Client.DisarmAsync(
                WatchdogSamples.LeaseId,
                TimeSpan.FromSeconds(1),
                CancellationToken.None);
            await firstProcess.DisposeEntered.Task.ConfigureAwait(false);

            var rearmTask = harness.Client.ArmAsync(
                WatchdogSamples.Request(),
                TimeSpan.FromSeconds(1),
                CancellationToken.None);

            Assert.False(rearmTask.IsCompleted);
            Assert.Equal(1, launcher.StartCalls);

            firstProcess.AllowDispose.TrySetResult(true);
            await disarmTask.ConfigureAwait(false);
            var rearmed = await rearmTask.ConfigureAwait(false);

            Assert.NotNull(rearmed);
            Assert.Equal(secondProcess.Id, rearmed!.WatchdogProcessId);
            Assert.Equal(2, launcher.StartCalls);
        }
        finally
        {
            firstProcess.AllowDispose.TrySetResult(true);
            await harness.Client.DisposeAsync().ConfigureAwait(false);
            launcher.DisposeObservers();
        }
    }

    [Fact]
    public async Task Dispose_WhileArmedOnlyClosesWrappersWithoutSignalingOrTerminating()
    {
        var process = new FakeWatchdogProcess { Id = 4321 };
        var launcher = new FakeWatchdogProcessLauncher(process);
        var harness = WatchdogClientHarness.Create(launcher);

        try
        {
            Assert.NotNull(
                await harness.Client.ArmAsync(
                        WatchdogSamples.Request(),
                        TimeSpan.FromSeconds(1),
                        CancellationToken.None)
                    .ConfigureAwait(false));

            await harness.Client.DisposeAsync().ConfigureAwait(false);

            Assert.False(launcher.StopObserver!.WaitOne(0));
            Assert.False(process.Terminated);
            Assert.Equal(0, process.WaitForExitCalls);
            Assert.True(process.Disposed);
        }
        finally
        {
            await harness.Client.DisposeAsync().ConfigureAwait(false);
            launcher.DisposeObservers();
        }
    }
}

internal sealed class WatchdogClientHarness
{
    private WatchdogClientHarness(
        TaskbarWatchdogClient client,
        string helperPath)
    {
        Client = client;
        HelperPath = helperPath;
    }

    public TaskbarWatchdogClient Client { get; }

    public string HelperPath { get; }

    public static WatchdogClientHarness Create(
        FakeWatchdogProcessLauncher launcher,
        Func<string, IWatchdogEvent>? eventFactory = null)
    {
        var helperPath = Path.Combine(
            WatchdogSamples.AppDataRoot,
            "fake-helper",
            "MacDock.Watchdog.exe");
        var client = eventFactory is null
            ? new TaskbarWatchdogClient(helperPath, WatchdogSamples.AppDataRoot, launcher)
            : new TaskbarWatchdogClient(
                helperPath,
                WatchdogSamples.AppDataRoot,
                launcher,
                eventFactory);
        return new WatchdogClientHarness(client, helperPath);
    }
}
