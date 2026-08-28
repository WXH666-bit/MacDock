using MacDock.Core.Services.Taskbar;
using Xunit;

namespace MacDock.Tests;

public sealed class TaskbarCoordinatorTests
{
    [Fact]
    public async Task TrayPreference_SavePreservesOtherSettings()
    {
        var harness = CoordinatorHarness.Create(
            persistedTaskbarSetting: true,
            menuBarReserveWorkArea: true);

        try
        {
            var result = await harness.Coordinator
                .SaveTrayTakeoverPreferenceAsync(true);
            var saved = harness.Settings.Load();

            Assert.True(result.Succeeded);
            Assert.True(result.Enabled);
            Assert.True(saved.TrayTakeover);
            Assert.True(saved.HideWindowsTaskbar);
            Assert.True(saved.MenuBarReserveWorkArea);
            Assert.Equal(0, harness.Lease.AcquireCalls);
            Assert.Equal(0, harness.Lease.ReleaseCalls);
            Assert.Equal(0, harness.Lease.ReconcileCalls);
        }
        finally
        {
            await harness.Coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task TrayPreference_ThenTaskbarEnablePreservesBothPreferences()
    {
        var harness = CoordinatorHarness.Create();

        try
        {
            var tray = await harness.Coordinator
                .SaveTrayTakeoverPreferenceAsync(true);
            var taskbar = await harness.Coordinator.SetEnabledAsync(true);
            var saved = harness.Settings.Load();

            Assert.True(tray.Succeeded);
            Assert.True(taskbar.Succeeded);
            Assert.True(saved.TrayTakeover);
            Assert.True(saved.HideWindowsTaskbar);
            Assert.Equal(1, harness.Lease.AcquireCalls);
        }
        finally
        {
            await harness.Coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task TrayPreference_TaskbarPersistencePendingRejectsAdditionalWrite()
    {
        var harness = CoordinatorHarness.Create(releaseResult: false);
        harness.Settings.SaveException = new IOException("fake taskbar save failure");

        try
        {
            var taskbar = await harness.Coordinator.SetEnabledAsync(true);
            Assert.False(taskbar.Succeeded);
            Assert.True(taskbar.Enabled);

            harness.Settings.SaveException = null;
            var savesBeforeTray = harness.Settings.SaveCalls;
            var tray = await harness.Coordinator
                .SaveTrayTakeoverPreferenceAsync(true);

            Assert.False(tray.Succeeded);
            Assert.Contains("pending", tray.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(savesBeforeTray, harness.Settings.SaveCalls);
            Assert.False(harness.Settings.Load().TrayTakeover);
        }
        finally
        {
            await harness.Coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task TrayPreference_DisposedCoordinatorDoesNotSave()
    {
        var harness = CoordinatorHarness.Create();
        await harness.Coordinator.DisposeAsync();

        var result = await harness.Coordinator
            .SaveTrayTakeoverPreferenceAsync(true);

        Assert.False(result.Succeeded);
        Assert.False(result.Enabled);
        Assert.Contains("shutting down", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.Settings.SaveCalls);
    }

    [Fact]
    public async Task TrayPreference_SaveFailureRollsBackInMemoryPreference()
    {
        var harness = CoordinatorHarness.Create();
        harness.Settings.SaveException = new IOException("fake preference save failure");

        try
        {
            var failed = await harness.Coordinator
                .SaveTrayTakeoverPreferenceAsync(true);

            Assert.False(failed.Succeeded);
            Assert.False(failed.Enabled);
            Assert.Contains("fake preference save failure", failed.Error);

            harness.Settings.SaveException = null;
            var unchanged = await harness.Coordinator
                .SaveTrayTakeoverPreferenceAsync(false);

            Assert.True(unchanged.Succeeded);
            Assert.False(unchanged.Enabled);
            Assert.Equal(1, harness.Settings.SaveCalls);
            Assert.False(harness.Settings.Load().TrayTakeover);
        }
        finally
        {
            await harness.Coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task TrayPreference_UnavailableStartupBlocksNewOptIn()
    {
        var harness = CoordinatorHarness.Create(
            changesAllowed: false,
            unavailableReason: "startup recovery was not verified");

        try
        {
            var result = await harness.Coordinator
                .SaveTrayTakeoverPreferenceAsync(true);

            Assert.False(result.Succeeded);
            Assert.False(result.Enabled);
            Assert.Contains("startup recovery", result.Error);
            Assert.Equal(0, harness.Settings.SaveCalls);
        }
        finally
        {
            await harness.Coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task TrayPreference_UnavailableStartupStillAllowsOptOut()
    {
        var harness = CoordinatorHarness.Create(
            changesAllowed: false,
            unavailableReason: "startup recovery was not verified",
            persistedTrayTakeover: true);

        try
        {
            var result = await harness.Coordinator
                .SaveTrayTakeoverPreferenceAsync(false);

            Assert.True(result.Succeeded);
            Assert.False(result.Enabled);
            Assert.False(harness.Settings.Load().TrayTakeover);
            Assert.Equal(1, harness.Settings.SaveCalls);
        }
        finally
        {
            await harness.Coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task Enable_WhenAcquireFails_DoesNotPersistTrue()
    {
        var harness = CoordinatorHarness.Create(acquireResult: false);

        try
        {
            var result = await harness.Coordinator.SetEnabledAsync(true);

            Assert.False(result.Succeeded);
            Assert.False(result.Enabled);
            Assert.False(harness.Settings.Load().HideWindowsTaskbar);
            Assert.Equal(1, harness.Lease.AcquireCalls);
            Assert.DoesNotContain("save-true", harness.Events);
        }
        finally
        {
            await harness.Coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task PersistedTruePreference_StartsDisabledUntilExplicitAcquire()
    {
        var harness = CoordinatorHarness.Create(persistedTaskbarSetting: true);

        try
        {
            Assert.False(harness.Coordinator.IsEnabled);
            Assert.True(harness.Settings.Load().HideWindowsTaskbar);
            Assert.Equal(0, harness.Lease.AcquireCalls);

            var result = await harness.Coordinator.SetEnabledAsync(true);

            Assert.True(result.Succeeded);
            Assert.True(harness.Coordinator.IsEnabled);
            Assert.Equal(1, harness.Lease.AcquireCalls);
        }
        finally
        {
            await harness.Coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task Enable_PersistsTrueOnlyAfterSuccessfulAcquire()
    {
        var events = new List<string>();
        var harness = CoordinatorHarness.Create(events);

        try
        {
            var result = await harness.Coordinator.SetEnabledAsync(true);

            Assert.True(result.Succeeded);
            Assert.True(result.Enabled);
            Assert.True(events.IndexOf("acquire") < events.IndexOf("save-true"));
        }
        finally
        {
            await harness.Coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task Disable_ReleasesBeforeSavingFalse()
    {
        var events = new List<string>();
        var harness = CoordinatorHarness.Create(events);

        try
        {
            Assert.True(
                (await harness.Coordinator.SetEnabledAsync(true))
                .Succeeded);
            events.Clear();

            var result = await harness.Coordinator.SetEnabledAsync(false);

            Assert.True(result.Succeeded);
            Assert.False(result.Enabled);
            Assert.True(events.IndexOf("release") < events.IndexOf("save-false"));
        }
        finally
        {
            await harness.Coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task Enable_WhenAcquireThrows_ReturnsReadableFailureWithoutSaving()
    {
        var harness = CoordinatorHarness.Create();
        harness.Lease.AcquireException = new IOException("fake acquire failure");

        try
        {
            var result = await harness.Coordinator.SetEnabledAsync(true);

            Assert.False(result.Succeeded);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
            Assert.False(harness.Settings.Load().HideWindowsTaskbar);
            Assert.DoesNotContain("save-true", harness.Events);
        }
        finally
        {
            await harness.Coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task Enable_WhenAcquireIsCanceled_DoesNotPersistTrue()
    {
        var harness = CoordinatorHarness.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        try
        {
            var result = await harness.Coordinator.SetEnabledAsync(
                    true,
                    cancellation.Token);

            Assert.False(result.Succeeded);
            Assert.False(harness.Settings.Load().HideWindowsTaskbar);
            Assert.DoesNotContain("save-true", harness.Events);
        }
        finally
        {
            await harness.Coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task Enable_WhenSavingTrueFails_ReturnsReadableFailure()
    {
        var harness = CoordinatorHarness.Create();
        harness.Settings.SaveException = new IOException("fake settings save failure");

        try
        {
            var result = await harness.Coordinator.SetEnabledAsync(true);

            Assert.False(result.Succeeded);
            Assert.False(string.IsNullOrWhiteSpace(result.Error));
            Assert.True(harness.Lease.AcquireCalls >= 1);
            Assert.False(harness.Settings.Load().HideWindowsTaskbar);
        }
        finally
        {
            await harness.Coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task Enable_WhenSavingTrueFails_ReleasesAndReturnsDisabledAfterRollback()
    {
        var harness = CoordinatorHarness.Create();
        harness.Settings.SaveException = new IOException("fake settings save failure");

        try
        {
            var result = await harness.Coordinator.SetEnabledAsync(true);

            Assert.False(result.Succeeded);
            Assert.False(result.Enabled);
            Assert.False(harness.Coordinator.IsEnabled);
            Assert.Equal(1, harness.Lease.AcquireCalls);
            Assert.Equal(1, harness.Lease.ReleaseCalls);
            Assert.Equal(TaskbarLeaseState.Released, harness.Lease.State);
            Assert.False(harness.Settings.Load().HideWindowsTaskbar);
        }
        finally
        {
            await harness.Coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task Enable_WhenSavingTrueAndRollbackReleaseFail_StaysEnabledForRecoveryRetry()
    {
        var harness = CoordinatorHarness.Create();
        harness.Settings.SaveException = new IOException("fake settings save failure");
        harness.Lease.ReleaseResult = false;

        try
        {
            var result = await harness.Coordinator.SetEnabledAsync(true);

            Assert.False(result.Succeeded);
            Assert.True(result.Enabled);
            Assert.True(harness.Coordinator.IsEnabled);
            Assert.False(string.IsNullOrWhiteSpace(harness.Coordinator.LastError));
            Assert.Equal(1, harness.Lease.AcquireCalls);
            Assert.Equal(1, harness.Lease.ReleaseCalls);
            Assert.Equal(TaskbarLeaseState.RecoveryPending, harness.Lease.State);
            Assert.False(harness.Settings.Load().HideWindowsTaskbar);
        }
        finally
        {
            await harness.Coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task Disable_WhenReleaseFails_LeavesEffectiveAndPersistedStateEnabled()
    {
        var harness = CoordinatorHarness.Create();

        try
        {
            Assert.True(
                (await harness.Coordinator.SetEnabledAsync(true))
                .Succeeded);
            harness.Lease.ReleaseResult = false;

            var result = await harness.Coordinator.SetEnabledAsync(false);

            Assert.False(result.Succeeded);
            Assert.True(harness.Coordinator.IsEnabled);
            Assert.True(harness.Settings.Load().HideWindowsTaskbar);
            Assert.DoesNotContain("save-false", harness.Events);
        }
        finally
        {
            await harness.Coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task Disable_WhenSavingFalseFails_PrioritizesVisibleLeaseAndDoesNotReacquire()
    {
        var harness = CoordinatorHarness.Create();

        try
        {
            Assert.True(
                (await harness.Coordinator.SetEnabledAsync(true))
                .Succeeded);
            harness.Settings.SaveException = new IOException("fake settings save failure");

            var result = await harness.Coordinator.SetEnabledAsync(false);

            Assert.False(result.Succeeded);
            Assert.False(result.Enabled);
            Assert.False(harness.Coordinator.IsEnabled);
            Assert.Equal(TaskbarLeaseState.Released, harness.Lease.State);
            Assert.Equal(1, harness.Lease.AcquireCalls);
            Assert.Equal(1, harness.Lease.ReleaseCalls);
            Assert.True(harness.Settings.Load().HideWindowsTaskbar);
            var saveCallsBeforeRetry = harness.Settings.SaveCalls;

            harness.Settings.SaveException = null;

            var retry = await harness.Coordinator.SetEnabledAsync(false);
            Assert.True(retry.Succeeded);
            Assert.False(retry.Enabled);
            Assert.False(harness.Coordinator.IsEnabled);
            Assert.False(harness.Settings.Load().HideWindowsTaskbar);
            Assert.Equal(saveCallsBeforeRetry + 1, harness.Settings.SaveCalls);
            Assert.Equal(1, harness.Lease.AcquireCalls);
            Assert.Equal(1, harness.Lease.ReleaseCalls);
        }
        finally
        {
            await harness.Coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task Enable_AfterPendingDisableSaveRetry_ContinuesWithRequestedEnable()
    {
        var harness = CoordinatorHarness.Create();

        try
        {
            Assert.True(
                (await harness.Coordinator.SetEnabledAsync(true))
                .Succeeded);

            harness.Settings.SaveException = new IOException("fake settings save failure");
            var failedDisable = await harness.Coordinator.SetEnabledAsync(false);

            Assert.False(failedDisable.Succeeded);
            Assert.False(failedDisable.Enabled);
            Assert.False(harness.Coordinator.IsEnabled);
            Assert.Equal(TaskbarLeaseState.Released, harness.Lease.State);

            harness.Settings.SaveException = null;
            var enable = await harness.Coordinator.SetEnabledAsync(true);

            Assert.True(enable.Succeeded);
            Assert.True(enable.Enabled);
            Assert.True(harness.Coordinator.IsEnabled);
            Assert.Equal(TaskbarLeaseState.Active, harness.Lease.State);
            Assert.Equal(2, harness.Lease.AcquireCalls);
            Assert.Equal(1, harness.Lease.ReleaseCalls);
            Assert.True(harness.Settings.Load().HideWindowsTaskbar);
            Assert.Equal(4, harness.Settings.SaveCalls);
        }
        finally
        {
            await harness.Coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task DuplicateEnableRequests_AreIdempotent()
    {
        var harness = CoordinatorHarness.Create();

        try
        {
            var first = await harness.Coordinator.SetEnabledAsync(true);
            var second = await harness.Coordinator.SetEnabledAsync(true);

            Assert.True(first.Succeeded);
            Assert.True(second.Succeeded);
            Assert.True(second.Enabled);
            Assert.Equal(1, harness.Lease.AcquireCalls);
            Assert.Equal(1, harness.Settings.SaveCalls);
        }
        finally
        {
            await harness.Coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task DuplicateDisableRequests_AreIdempotent()
    {
        var harness = CoordinatorHarness.Create();

        try
        {
            Assert.True(
                (await harness.Coordinator.SetEnabledAsync(true))
                .Succeeded);

            var first = await harness.Coordinator.SetEnabledAsync(false);
            var second = await harness.Coordinator.SetEnabledAsync(false);

            Assert.True(first.Succeeded);
            Assert.True(second.Succeeded);
            Assert.False(second.Enabled);
            Assert.Equal(1, harness.Lease.ReleaseCalls);
            Assert.Equal(2, harness.Settings.SaveCalls);
            Assert.False(harness.Coordinator.IsEnabled);
        }
        finally
        {
            await harness.Coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task Reconcile_WhenDisabled_DoesNotTouchLease()
    {
        var harness = CoordinatorHarness.Create();

        try
        {
            var reconciled = await harness.Coordinator.ReconcileAsync();

            Assert.False(reconciled);
            Assert.Equal(0, harness.Lease.ReconcileCalls);
        }
        finally
        {
            await harness.Coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task Reconcile_WhenLeaseReportsFailure_ReturnsFalseAndKeepsEffectiveState()
    {
        var harness = CoordinatorHarness.Create(reconcileResult: false);

        try
        {
            Assert.True(
                (await harness.Coordinator.SetEnabledAsync(true))
                .Succeeded);

            var result = await harness.Coordinator.ReconcileAsync();

            Assert.False(result);
            Assert.True(harness.Coordinator.IsEnabled);
            Assert.Equal(1, harness.Lease.ReconcileCalls);
        }
        finally
        {
            await harness.Coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task Reconcile_WhenLeaseThrows_ReturnsFalseWithReadableError()
    {
        var harness = CoordinatorHarness.Create();
        harness.Lease.ReconcileException = new IOException("fake reconcile failure");

        try
        {
            Assert.True(
                (await harness.Coordinator.SetEnabledAsync(true))
                .Succeeded);

            var result = await harness.Coordinator.ReconcileAsync();

            Assert.False(result);
            Assert.True(harness.Coordinator.IsEnabled);
            Assert.False(string.IsNullOrWhiteSpace(harness.Coordinator.LastError));
            Assert.Equal(1, harness.Lease.ReconcileCalls);
        }
        finally
        {
            await harness.Coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task UnavailableStartup_FailsClosedWithoutLeaseAccess()
    {
        var harness = CoordinatorHarness.Create(
            changesAllowed: false,
            unavailableReason: "startup recovery was not verified");

        try
        {
            var toggle = await harness.Coordinator.SetEnabledAsync(true);
            var reconciled = await harness.Coordinator.ReconcileAsync();

            Assert.False(toggle.Succeeded);
            Assert.Contains("startup recovery", toggle.Error, StringComparison.OrdinalIgnoreCase);
            Assert.False(reconciled);
            Assert.Equal(0, harness.Lease.AcquireCalls);
            Assert.Equal(0, harness.Lease.ReconcileCalls);
            Assert.Equal(0, harness.Settings.SaveCalls);
        }
        finally
        {
            await harness.Coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task UnavailableOrDisposedFailure_PublishesLastError()
    {
        var unavailable = CoordinatorHarness.Create(
            changesAllowed: false,
            unavailableReason: "startup recovery was not verified");

        try
        {
            var reconciled = await unavailable.Coordinator.ReconcileAsync();

            Assert.False(reconciled);
            Assert.Contains(
                "startup recovery",
                unavailable.Coordinator.LastError,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await unavailable.Coordinator.DisposeAsync();
        }

        var disposed = CoordinatorHarness.Create();
        await disposed.Coordinator.DisposeAsync();

        var toggle = await disposed.Coordinator.SetEnabledAsync(true);

        Assert.False(toggle.Succeeded);
        Assert.Equal(toggle.Error, disposed.Coordinator.LastError);
        Assert.Contains(
            "shutting down",
            disposed.Coordinator.LastError,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IdempotentSuccess_ClearsPreviousLastError()
    {
        var harness = CoordinatorHarness.Create();

        try
        {
            Assert.True(
                (await harness.Coordinator.SetEnabledAsync(true))
                .Succeeded);
            harness.Lease.ReleaseResult = false;

            var failedDisable = await harness.Coordinator.SetEnabledAsync(false);

            Assert.False(failedDisable.Succeeded);
            Assert.False(string.IsNullOrWhiteSpace(harness.Coordinator.LastError));

            var duplicateEnable = await harness.Coordinator.SetEnabledAsync(true);

            Assert.True(duplicateEnable.Succeeded);
            Assert.True(duplicateEnable.Enabled);
            Assert.Null(harness.Coordinator.LastError);
            Assert.Equal(1, harness.Lease.ReleaseCalls);
        }
        finally
        {
            await harness.Coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReconcileBurst_IsSerialized()
    {
        var harness = CoordinatorHarness.Create();
        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var maximumRunning = 0;

        harness.Lease.ReconcileHandler = async cancellationToken =>
        {
            var current = Interlocked.Increment(ref running);
            UpdateMaximum(ref maximumRunning, current);
            entered.TrySetResult(true);
            try
            {
                await release.Task.WaitAsync(cancellationToken);
                return true;
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        };

        try
        {
            Assert.True(
                (await harness.Coordinator.SetEnabledAsync(true))
                .Succeeded);

            var requests = Enumerable.Range(0, 3)
                .Select(_ => harness.Coordinator.ReconcileAsync())
                .ToArray();
            await entered.Task;
            Assert.Equal(1, Volatile.Read(ref maximumRunning));

            release.TrySetResult(true);
            var results = await Task.WhenAll(requests);

            Assert.All(results, Assert.True);
            Assert.Equal(3, harness.Lease.ReconcileCalls);
            Assert.Equal(1, Volatile.Read(ref maximumRunning));
        }
        finally
        {
            release.TrySetResult(true);
            await harness.Coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task QueuedCancellationReturnsFailureWithoutTouchingLease()
    {
        var harness = CoordinatorHarness.Create();
        var entered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Lease.ReconcileHandler = async cancellationToken =>
        {
            entered.TrySetResult(true);
            await release.Task.WaitAsync(cancellationToken);
            return true;
        };

        try
        {
            Assert.True(
                (await harness.Coordinator.SetEnabledAsync(true))
                .Succeeded);

            var first = harness.Coordinator.ReconcileAsync();
            await entered.Task;

            using var toggleCancellation = new CancellationTokenSource();
            var queuedToggle = harness.Coordinator.SetEnabledAsync(
                false,
                toggleCancellation.Token);
            toggleCancellation.Cancel();
            var toggleResult = await queuedToggle;

            Assert.False(toggleResult.Succeeded);
            Assert.True(toggleResult.Enabled);
            Assert.Contains("canceled", toggleResult.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, harness.Lease.ReleaseCalls);

            using var reconcileCancellation = new CancellationTokenSource();
            var queuedReconcile = harness.Coordinator.ReconcileAsync(
                reconcileCancellation.Token);
            reconcileCancellation.Cancel();
            Assert.False(await queuedReconcile);
            Assert.Equal(1, harness.Lease.ReconcileCalls);

            release.TrySetResult(true);
            Assert.True(await first);
        }
        finally
        {
            release.TrySetResult(true);
            await harness.Coordinator.DisposeAsync();
        }
    }

    [Fact]
    public async Task Dispose_RacingQueuedReconcile_DisposesLeaseOnceAndClosesCoordination()
    {
        var harness = CoordinatorHarness.Create();
        var firstReconcileEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstReconcile = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        harness.Lease.ReconcileHandler = async cancellationToken =>
        {
            firstReconcileEntered.TrySetResult(true);
            await allowFirstReconcile.Task.WaitAsync(cancellationToken);
            return true;
        };

        Assert.True(
            (await harness.Coordinator.SetEnabledAsync(true))
            .Succeeded);

        var first = harness.Coordinator.ReconcileAsync();
        await firstReconcileEntered.Task;
        var queued = harness.Coordinator.ReconcileAsync();
        var disposing = harness.Coordinator.DisposeAsync().AsTask();

        allowFirstReconcile.TrySetResult(true);

        Assert.True(await first);
        await disposing;
        Assert.False(await queued);
        Assert.Equal(1, harness.Lease.ReconcileCalls);
        Assert.Equal(1, harness.Lease.DisposeCalls);

        var reconcileAttempts = harness.Lease.ReconcileAttempts;
        Assert.False(await harness.Coordinator.ReconcileAsync());
        Assert.Equal(reconcileAttempts, harness.Lease.ReconcileAttempts);
    }

    [Fact]
    public async Task Dispose_LeaseDisposeReentryDoesNotRunUnderDisposeLock()
    {
        var harness = CoordinatorHarness.Create();
        Assert.True(
            (await harness.Coordinator.SetEnabledAsync(true))
            .Succeeded);

        var barrier = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reentered = new TaskCompletionSource<Task>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Lease.DisposeBarrier = barrier.Task;
        harness.Lease.DisposeCallback = () =>
        {
            reentered.TrySetResult(harness.Coordinator.DisposeAsync().AsTask());
        };

        var disposing = Task.Run(() => harness.Coordinator.DisposeAsync().AsTask());
        var reentryTask = await reentered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(reentryTask.IsCompleted);
        barrier.TrySetResult(true);
        await disposing;
        await reentryTask;

        Assert.Equal(1, harness.Lease.DisposeCalls);
    }

    [Fact]
    public async Task Dispose_ReleasesActiveLeaseWithoutClearingPersistedOptIn()
    {
        var harness = CoordinatorHarness.Create();

        Assert.True(
            (await harness.Coordinator.SetEnabledAsync(true))
            .Succeeded);

        await harness.Coordinator.DisposeAsync();
        await harness.Coordinator.DisposeAsync();

        Assert.True(harness.Settings.Load().HideWindowsTaskbar);
        Assert.Equal(1, harness.Lease.DisposeCalls);
        Assert.Equal(TaskbarLeaseState.Released, harness.Lease.State);
        Assert.False(harness.Coordinator.IsEnabled);

        var reconcileAttempts = harness.Lease.ReconcileAttempts;
        Assert.False(await harness.Coordinator.ReconcileAsync());
        Assert.Equal(reconcileAttempts, harness.Lease.ReconcileAttempts);
    }

    [Fact]
    public async Task Dispose_WhenReleaseFails_IsIdempotentAndLeavesRecoveryState()
    {
        var harness = CoordinatorHarness.Create();

        Assert.True(
            (await harness.Coordinator.SetEnabledAsync(true))
            .Succeeded);
        harness.Lease.ReleaseResult = false;

        await harness.Coordinator.DisposeAsync();
        await harness.Coordinator.DisposeAsync();

        Assert.Equal(1, harness.Lease.DisposeCalls);
        Assert.Equal(TaskbarLeaseState.RecoveryPending, harness.Lease.State);
        Assert.True(harness.Coordinator.IsEnabled);
        Assert.True(harness.Settings.Load().HideWindowsTaskbar);

        var reconcileAttempts = harness.Lease.ReconcileAttempts;
        Assert.False(await harness.Coordinator.ReconcileAsync());
        Assert.Equal(reconcileAttempts, harness.Lease.ReconcileAttempts);
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        while (true)
        {
            var observed = Volatile.Read(ref target);
            if (value <= observed)
                return;

            if (Interlocked.CompareExchange(ref target, value, observed) == observed)
                return;
        }
    }
}
