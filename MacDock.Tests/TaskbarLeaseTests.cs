using MacDock.Core.Interop;
using MacDock.Core.Services.Taskbar;
using Xunit;

namespace MacDock.Tests;

#pragma warning disable xUnit1030
public sealed class TaskbarLeaseTests
{
    [Fact]
    public async Task Acquire_ArmsThenJournalsBeforeHiding()
    {
        var events = new List<string>();
        var harness = LeaseHarness.Create(events, hideChangesVisibility: true);

        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));

        Assert.Equal(
            new[]
            {
                "lease-lock",
                "journal-read",
                "capture",
                "guard-arm",
                "journal-prepared",
                "journal-hide-pending",
                "hide",
                "journal-active",
            },
            events);
        Assert.Equal(TaskbarLeaseState.Active, harness.Lease.State);
    }

    [Fact]
    public async Task Acquire_WhenLockCannotBeTaken_DoesNotArmOrHide()
    {
        var harness = LeaseHarness.Create(lockAcquireSucceeds: false);

        Assert.False(await harness.Lease.AcquireAsync().ConfigureAwait(false));

        Assert.Equal(0, harness.Guard.ArmCalls);
        Assert.Equal(0, harness.Platform.HideCalls);
        Assert.False(harness.Journal.Exists);
        Assert.Equal(TaskbarLeaseState.Released, harness.Lease.State);
    }

    [Fact]
    public async Task Acquire_WhenCaptureFails_ReleasesLockWithoutArmingOrHiding()
    {
        var harness = LeaseHarness.Create(captureSucceeds: false);

        Assert.False(await harness.Lease.AcquireAsync().ConfigureAwait(false));

        Assert.Equal(0, harness.Guard.ArmCalls);
        Assert.Equal(0, harness.Platform.HideCalls);
        Assert.False(harness.Journal.Exists);
        Assert.False(harness.Lock.IsOwned);
        Assert.Equal(TaskbarLeaseState.Released, harness.Lease.State);
    }

    [Fact]
    public async Task Acquire_WhenGuardIsNotReady_DoesNotJournalOrHide()
    {
        var harness = LeaseHarness.Create(guardArmSucceeds: false);

        Assert.False(await harness.Lease.AcquireAsync().ConfigureAwait(false));

        Assert.Equal(1, harness.Guard.ArmCalls);
        Assert.Equal(0, harness.Journal.WriteCalls);
        Assert.Equal(0, harness.Platform.HideCalls);
        Assert.False(harness.Lock.IsOwned);
        Assert.Equal(TaskbarLeaseState.Released, harness.Lease.State);
    }

    [Fact]
    public async Task Acquire_WhenWindowWasAlreadyInvisible_RemainsActiveWithoutHide()
    {
        var harness = LeaseHarness.Create(originallyVisible: false);

        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));

        Assert.Equal(TaskbarLeaseState.Active, harness.Lease.State);
        Assert.Equal(0, harness.Platform.HideCalls);
        Assert.Equal(TaskbarWindowMutationState.Unchanged, harness.Journal.Document!.Windows[0].MutationState);

        Assert.True(await harness.Lease.ReleaseAsync().ConfigureAwait(false));
        Assert.Equal(0, harness.Platform.ShowCalls);
    }

    [Fact]
    public async Task Acquire_WhenExternalActorHidBeforeCall_DoesNotClaimOrRestoreIt()
    {
        var harness = LeaseHarness.Create();
        harness.Platform.SetWindowVisible(42, visible: false);

        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));

        Assert.Equal(TaskbarWindowMutationState.Unchanged, harness.Journal.Document!.Windows[0].MutationState);
        Assert.True(await harness.Lease.ReleaseAsync().ConfigureAwait(false));
        Assert.Equal(0, harness.Platform.ShowCalls);
    }

    [Fact]
    public async Task Acquire_WhenHideIsVerifiedNoChange_FailsAndCleansUp()
    {
        var harness = LeaseHarness.Create(hideChangesVisibility: false);

        Assert.False(await harness.Lease.AcquireAsync().ConfigureAwait(false));

        Assert.Equal(TaskbarLeaseState.Released, harness.Lease.State);
        Assert.False(harness.Journal.Exists);
        Assert.True(harness.Guard.WasDisarmed);
        Assert.False(harness.Lock.IsOwned);
        Assert.Equal(1, harness.Platform.HideCalls);
        Assert.Equal(0, harness.Platform.ShowCalls);
    }

    [Fact]
    public async Task Acquire_WhenHideOutcomeIsIndeterminate_RetainsRecoveryState()
    {
        var harness = LeaseHarness.Create();
        harness.Platform.DestroyAfterHide = true;

        Assert.False(await harness.Lease.AcquireAsync().ConfigureAwait(false));

        Assert.Equal(TaskbarLeaseState.RecoveryPending, harness.Lease.State);
        Assert.True(harness.Journal.Exists);
        Assert.Equal(
            TaskbarWindowMutationState.HidePending,
            harness.Journal.Document!.Windows[0].MutationState);
        Assert.False(harness.Guard.WasDisarmed);
        Assert.True(harness.Lock.IsOwned);
    }

    [Fact]
    public async Task Acquire_WhenJournalFailsAfterHide_RollsBackVerifiedLeaseMutation()
    {
        var harness = LeaseHarness.Create(failAfterHide: true);

        Assert.False(await harness.Lease.AcquireAsync().ConfigureAwait(false));

        Assert.Equal(TaskbarLeaseState.Released, harness.Lease.State);
        Assert.False(harness.Journal.Exists);
        Assert.True(harness.Guard.WasDisarmed);
        Assert.False(harness.Lock.IsOwned);
        Assert.True(harness.Platform.IsWindowVisible(42));
        Assert.Equal(1, harness.Platform.ShowCalls);
    }

    [Fact]
    public async Task Acquire_WhenRollbackCannotBeVerified_KeepsGuardAndLockOwned()
    {
        var harness = LeaseHarness.Create(
            rollbackChangesVisibility: false,
            failAfterHide: true);

        Assert.False(await harness.Lease.AcquireAsync().ConfigureAwait(false));

        Assert.Equal(TaskbarLeaseState.RecoveryPending, harness.Lease.State);
        Assert.True(harness.Journal.Exists);
        Assert.False(harness.Guard.WasDisarmed);
        Assert.True(harness.Lock.IsOwned);
    }

    [Fact]
    public async Task Reconcile_OnlyRunsForAnActiveLease()
    {
        var harness = LeaseHarness.Create();

        Assert.False(await harness.Lease.ReconcileAsync().ConfigureAwait(false));

        Assert.Equal(0, harness.Platform.HideCalls);
        Assert.False(harness.Journal.Exists);
    }

    [Fact]
    public async Task Reconcile_ForSameFullIdentityIsIdempotent()
    {
        var harness = LeaseHarness.Create();
        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        var writesBefore = harness.Journal.WriteCalls;
        harness.Events.Clear();

        Assert.True(await harness.Lease.ReconcileAsync().ConfigureAwait(false));

        Assert.Equal(1, harness.Platform.HideCalls);
        Assert.Equal(writesBefore, harness.Journal.WriteCalls);
        Assert.Single(harness.Journal.Document!.Windows);
        Assert.Equal(TaskbarLeaseState.Active, harness.Lease.State);
    }

    [Fact]
    public async Task Reconcile_ForExplorerReplacementPersistsNewGenerationBeforeHide()
    {
        var harness = LeaseHarness.Create();
        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        harness.Events.Clear();
        harness.Platform.ReplacePrimary(
            handle: 42,
            processId: 101,
            processStartTicks: 5678,
            monitor: 7,
            visible: true,
            showCommand: NativeMethods.SW_SHOW);

        Assert.True(await harness.Lease.ReconcileAsync().ConfigureAwait(false));

        Assert.Equal(2, harness.Journal.Document!.Generation);
        Assert.Equal(2, harness.Journal.Document.Windows.Count);
        Assert.Equal(
            TaskbarWindowMutationState.HiddenByLease,
            harness.Journal.Document.Windows[1].MutationState);
        Assert.True(
            harness.Events.IndexOf("journal-hide-pending")
            < harness.Events.IndexOf("hide"));
        Assert.Equal(2, harness.Platform.HideCalls);
    }

    [Fact]
    public async Task Release_AfterExplorerReplacementRestoresOnlyTheNewMatchingIdentity()
    {
        var harness = LeaseHarness.Create();
        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        harness.Platform.ReplacePrimary(
            handle: 42,
            processId: 101,
            processStartTicks: 5678,
            monitor: 7,
            visible: true,
            showCommand: NativeMethods.SW_SHOW);

        Assert.True(await harness.Lease.ReconcileAsync().ConfigureAwait(false));
        Assert.True(await harness.Lease.ReleaseAsync().ConfigureAwait(false));

        Assert.Equal(1, harness.Platform.ShowCalls);
        Assert.True(harness.Platform.IsWindowVisible(42));
        Assert.Equal(TaskbarLeaseState.Released, harness.Lease.State);
        Assert.False(harness.Journal.Exists);
    }

    [Fact]
    public async Task Reconcile_WhenVerifiedHideFails_RecordsUnchangedAndStaysActive()
    {
        var harness = LeaseHarness.Create();
        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        harness.Platform.ReplacePrimary(
            handle: 42,
            processId: 101,
            processStartTicks: 5678,
            monitor: 7,
            visible: true,
            showCommand: NativeMethods.SW_SHOW);
        harness.Platform.HideChangesVisibility = false;

        Assert.False(await harness.Lease.ReconcileAsync().ConfigureAwait(false));

        Assert.Equal(TaskbarLeaseState.Active, harness.Lease.State);
        Assert.Equal(
            TaskbarWindowMutationState.Unchanged,
            harness.Journal.Document!.Windows[1].MutationState);
        Assert.True(harness.Journal.Exists);
    }

    [Fact]
    public async Task Reconcile_WhenHideIsIndeterminateRetainsHidePending()
    {
        var harness = LeaseHarness.Create();
        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        harness.Platform.ReplacePrimary(
            handle: 42,
            processId: 101,
            processStartTicks: 5678,
            monitor: 7,
            visible: true,
            showCommand: NativeMethods.SW_SHOW);
        harness.Platform.DestroyAfterHide = true;

        Assert.False(await harness.Lease.ReconcileAsync().ConfigureAwait(false));

        Assert.Equal(TaskbarLeaseState.RecoveryPending, harness.Lease.State);
        Assert.Equal(
            TaskbarWindowMutationState.HidePending,
            harness.Journal.Document!.Windows[1].MutationState);
        Assert.True(harness.Lock.IsOwned);
    }

    [Fact]
    public async Task Release_RestoresAndCleansUpInSafeOrder()
    {
        var events = new List<string>();
        var harness = LeaseHarness.Create(events);
        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        events.Clear();

        Assert.True(await harness.Lease.ReleaseAsync().ConfigureAwait(false));

        Assert.Equal(TaskbarLeaseState.Released, harness.Lease.State);
        Assert.False(harness.Journal.Exists);
        Assert.True(harness.Guard.WasDisarmed);
        Assert.False(harness.Lock.IsOwned);
        Assert.True(harness.Platform.IsWindowVisible(42));
        Assert.True(events.IndexOf("show") < events.IndexOf("journal-delete"));
        Assert.True(events.IndexOf("journal-delete") < events.IndexOf("guard-disarm"));
        Assert.True(events.IndexOf("guard-disarm") < events.IndexOf("lock-release"));
    }

    [Fact]
    public async Task Release_AfterSafeCompletionIsIdempotentWithoutRepeatingMutations()
    {
        var harness = LeaseHarness.Create();
        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        Assert.True(await harness.Lease.ReleaseAsync().ConfigureAwait(false));

        var showCalls = harness.Platform.ShowCalls;
        var disarmCalls = harness.Guard.DisarmCalls;
        var lockDisposeCalls = harness.Lock.DisposeCalls;

        Assert.True(await harness.Lease.ReleaseAsync().ConfigureAwait(false));

        Assert.Equal(showCalls, harness.Platform.ShowCalls);
        Assert.Equal(disarmCalls, harness.Guard.DisarmCalls);
        Assert.Equal(lockDisposeCalls, harness.Lock.DisposeCalls);
        Assert.Equal(TaskbarLeaseState.Released, harness.Lease.State);
    }

    [Fact]
    public async Task Release_TreatsAlreadyVisibleAsSafeWithoutCallingShow()
    {
        var harness = LeaseHarness.Create();
        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        harness.Platform.SetWindowVisible(42, visible: true);

        Assert.True(await harness.Lease.ReleaseAsync().ConfigureAwait(false));

        Assert.Equal(0, harness.Platform.ShowCalls);
        Assert.Equal(TaskbarLeaseState.Released, harness.Lease.State);
        Assert.False(harness.Journal.Exists);
    }

    [Fact]
    public async Task Release_TreatsStaleIdentityAsSafeWithoutTouchingReusedWindow()
    {
        var harness = LeaseHarness.Create();
        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        harness.Platform.SetClassName(42, "ReusedWindow");

        Assert.True(await harness.Lease.ReleaseAsync().ConfigureAwait(false));

        Assert.Equal(0, harness.Platform.ShowCalls);
        Assert.Equal(TaskbarLeaseState.Released, harness.Lease.State);
        Assert.False(harness.Journal.Exists);
    }

    [Fact]
    public async Task Release_WhenRestoreFailsRetainsEverythingAndCanRetry()
    {
        var harness = LeaseHarness.Create(rollbackChangesVisibility: false);
        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));

        Assert.False(await harness.Lease.ReleaseAsync().ConfigureAwait(false));

        Assert.Equal(TaskbarLeaseState.RecoveryPending, harness.Lease.State);
        Assert.True(harness.Journal.Exists);
        Assert.False(harness.Guard.WasDisarmed);
        Assert.True(harness.Lock.IsOwned);

        harness.Platform.ShowChangesVisibility = true;
        Assert.True(await harness.Lease.ReleaseAsync().ConfigureAwait(false));

        Assert.Equal(TaskbarLeaseState.Released, harness.Lease.State);
        Assert.False(harness.Journal.Exists);
        Assert.True(harness.Guard.WasDisarmed);
        Assert.False(harness.Lock.IsOwned);
    }

    [Fact]
    public async Task Release_WhenGuardDisarmFailsRetainsEverythingAndCanRetry()
    {
        var harness = LeaseHarness.Create();
        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        harness.Guard.DisarmSucceeds = false;

        Assert.False(await harness.Lease.ReleaseAsync().ConfigureAwait(false));

        Assert.Equal(TaskbarLeaseState.RecoveryPending, harness.Lease.State);
        Assert.True(harness.Journal.Exists);
        Assert.True(harness.Lock.IsOwned);
        Assert.False(harness.Guard.WasDisarmed);

        harness.Guard.DisarmSucceeds = true;
        Assert.True(await harness.Lease.ReleaseAsync().ConfigureAwait(false));

        Assert.Equal(TaskbarLeaseState.Released, harness.Lease.State);
        Assert.False(harness.Journal.Exists);
        Assert.False(harness.Lock.IsOwned);
    }

    [Fact]
    public async Task DisposeAsync_UsesReleasePath()
    {
        var harness = LeaseHarness.Create();
        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));

        await harness.Lease.DisposeAsync().ConfigureAwait(false);

        Assert.Equal(TaskbarLeaseState.Released, harness.Lease.State);
        Assert.False(harness.Journal.Exists);
        Assert.True(harness.Guard.WasDisarmed);
        Assert.False(harness.Lock.IsOwned);
    }

    [Fact]
    public async Task DisposeAsync_WhenRecoveryIsPendingDoesNotDisarmOrReleaseLock()
    {
        var harness = LeaseHarness.Create(rollbackChangesVisibility: false);
        harness.Platform.ThrowAfterHide = true;
        Assert.False(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        Assert.Equal(TaskbarLeaseState.RecoveryPending, harness.Lease.State);

        await harness.Lease.DisposeAsync().ConfigureAwait(false);

        Assert.Equal(TaskbarLeaseState.RecoveryPending, harness.Lease.State);
        Assert.False(harness.Guard.WasDisarmed);
        Assert.False(harness.Guard.WasDisposed);
        Assert.True(harness.Lock.IsOwned);
        Assert.True(harness.Journal.Exists);
    }

    [Fact]
    public async Task Reconcile_CancellationBeforeSystemCallDoesNotHideLate()
    {
        var harness = LeaseHarness.Create();
        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        harness.Platform.ReplacePrimary(
            handle: 42,
            processId: 101,
            processStartTicks: 5678,
            monitor: 7,
            visible: true,
            showCommand: NativeMethods.SW_SHOW);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.False(
            await harness.Lease.ReconcileAsync(cancellation.Token).ConfigureAwait(false));

        Assert.Equal(1, harness.Platform.HideCalls);
        Assert.Equal(TaskbarLeaseState.Active, harness.Lease.State);
        Assert.Single(harness.Journal.Document!.Windows);
    }

    [Fact]
    public async Task Reconcile_CancellationAfterHideRollsBackTheKnownMutation()
    {
        var harness = LeaseHarness.Create();
        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        harness.Platform.ReplacePrimary(
            handle: 42,
            processId: 101,
            processStartTicks: 5678,
            monitor: 7,
            visible: true,
            showCommand: NativeMethods.SW_SHOW);
        harness.Platform.BlockHide = true;
        using var cancellation = new CancellationTokenSource();

        var reconcileTask = Task.Run(() => harness.Lease.ReconcileAsync(cancellation.Token));
        await harness.Platform.HideEntered.Task.ConfigureAwait(false);
        cancellation.Cancel();
        harness.Platform.AllowHide.TrySetResult(true);

        Assert.False(await reconcileTask.ConfigureAwait(false));
        Assert.True(harness.Platform.IsWindowVisible(42));
        Assert.Equal(TaskbarLeaseState.Active, harness.Lease.State);
        Assert.Single(harness.Journal.Document!.Windows);
    }

    [Fact]
    public async Task ReleaseAndReconcile_AreSerializedByTheLeaseGate()
    {
        var harness = LeaseHarness.Create();
        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        harness.Platform.ReplacePrimary(
            handle: 42,
            processId: 101,
            processStartTicks: 5678,
            monitor: 7,
            visible: true,
            showCommand: NativeMethods.SW_SHOW);
        harness.Platform.BlockHide = true;

        var reconcileTask = Task.Run(() => harness.Lease.ReconcileAsync());
        await harness.Platform.HideEntered.Task.ConfigureAwait(false);
        var releaseTask = harness.Lease.ReleaseAsync();
        Assert.False(releaseTask.IsCompleted);

        harness.Platform.AllowHide.TrySetResult(true);
        Assert.True(await reconcileTask.ConfigureAwait(false));
        Assert.True(await releaseTask.ConfigureAwait(false));
    }

    [Fact]
    public async Task ConcurrentReconcileEvents_DoNotAppendOrHideTwice()
    {
        var harness = LeaseHarness.Create();
        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        harness.Platform.ReplacePrimary(
            handle: 42,
            processId: 101,
            processStartTicks: 5678,
            monitor: 7,
            visible: true,
            showCommand: NativeMethods.SW_SHOW);

        var results = await Task.WhenAll(
                Task.Run(() => harness.Lease.ReconcileAsync()),
                Task.Run(() => harness.Lease.ReconcileAsync()))
            .ConfigureAwait(false);

        Assert.All(results, Assert.True);
        Assert.Equal(2, harness.Platform.HideCalls);
        Assert.Equal(2, harness.Journal.Document!.Windows.Count);
    }

    [Theory]
    [InlineData(TaskbarWindowMutationState.HidePending)]
    [InlineData(TaskbarWindowMutationState.HiddenByLease)]
    public async Task Acquire_WhenJournalAlreadyExists_FailsBeforeCaptureAndPreservesIt(
        TaskbarWindowMutationState oldMutationState)
    {
        var oldDocument = LeaseSamples.Active(
            "22222222-2222-2222-2222-222222222222",
            handle: 42)
            with
        {
            Windows =
            [LeaseSamples.Active(
                    "22222222-2222-2222-2222-222222222222",
                    handle: 42)
                .Windows[0] with { MutationState = oldMutationState }],
        };
        var events = new List<string>();
        var harness = LeaseHarness.Create(events, existingDocument: oldDocument);

        Assert.False(await harness.Lease.AcquireAsync().ConfigureAwait(false));

        Assert.Equal(TaskbarLeaseState.Released, harness.Lease.State);
        Assert.Equal(oldDocument.LeaseId, harness.Journal.Document!.LeaseId);
        Assert.Equal(oldDocument.Status, harness.Journal.Document.Status);
        Assert.Equal(oldDocument.Windows[0].MutationState, harness.Journal.Document.Windows[0].MutationState);
        Assert.Equal(1, harness.Journal.ReadCalls);
        Assert.DoesNotContain("capture", events);
        Assert.Equal(0, harness.Guard.ArmCalls);
        Assert.Equal(0, harness.Platform.HideCalls);
        Assert.False(harness.Lock.IsOwned);
    }

    [Fact]
    public async Task Acquire_WhenJournalReadFails_FailsClosedWithoutMutatingAnything()
    {
        var oldDocument = LeaseSamples.Active(
            "33333333-3333-3333-3333-333333333333",
            handle: 42);
        var events = new List<string>();
        var harness = LeaseHarness.Create(events, existingDocument: oldDocument);
        harness.Journal.ReadException = new IOException("read failed");

        Assert.False(await harness.Lease.AcquireAsync().ConfigureAwait(false));

        Assert.Equal(TaskbarLeaseState.Released, harness.Lease.State);
        Assert.Equal(1, harness.Journal.ReadCalls);
        Assert.Equal(0, harness.Journal.WriteCalls);
        Assert.Equal(0, harness.Journal.DeleteCalls);
        Assert.Equal(0, harness.Guard.ArmCalls);
        Assert.Equal(0, harness.Guard.DisarmCalls);
        Assert.Equal(0, harness.Platform.HideCalls);
        Assert.Equal(oldDocument.LeaseId, harness.Journal.Document!.LeaseId);
        Assert.False(harness.Lock.IsOwned);
    }

    [Fact]
    public async Task ReleaseThenAcquire_ReusesTheInjectedGuardWrapper()
    {
        var harness = LeaseHarness.Create();
        harness.Guard.RejectArmAfterDispose = true;

        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        Assert.True(await harness.Lease.ReleaseAsync().ConfigureAwait(false));
        Assert.Equal(0, harness.Guard.DisposeCalls);

        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        Assert.Equal(2, harness.Guard.ArmCalls);
        Assert.Equal(0, harness.Guard.DisposeCalls);
    }

    [Fact]
    public async Task Acquire_CaptureFailureCanBeRetriedWithTheSameGuardWrapper()
    {
        var harness = LeaseHarness.Create(captureSucceeds: false);
        harness.Guard.RejectArmAfterDispose = true;

        Assert.False(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        harness.Platform.SetClassName(42, "Shell_TrayWnd");

        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        Assert.Equal(0, harness.Guard.DisposeCalls);
    }

    [Fact]
    public async Task DisposeAsync_FromReleasedDisposesGuardExactlyOnceAndRejectsFutureUse()
    {
        var harness = LeaseHarness.Create();
        harness.Guard.RejectArmAfterDispose = true;

        await harness.Lease.DisposeAsync().ConfigureAwait(false);
        await harness.Lease.DisposeAsync().ConfigureAwait(false);

        Assert.Equal(1, harness.Guard.DisposeCalls);
        Assert.False(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        Assert.False(await harness.Lease.ReconcileAsync().ConfigureAwait(false));
        Assert.Equal(0, harness.Guard.ArmCalls);
        Assert.Equal(0, harness.Platform.HideCalls);
    }

    [Fact]
    public async Task DisposeAsync_AfterSafeReleaseDisposesGuardExactlyOnce()
    {
        var harness = LeaseHarness.Create();
        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        Assert.True(await harness.Lease.ReleaseAsync().ConfigureAwait(false));

        await harness.Lease.DisposeAsync().ConfigureAwait(false);
        await harness.Lease.DisposeAsync().ConfigureAwait(false);

        Assert.Equal(1, harness.Guard.DisposeCalls);
        Assert.Equal(TaskbarLeaseState.Released, harness.Lease.State);
    }

    [Fact]
    public async Task Acquire_WhenReleaseIsRequestedWhileGuardArmBlocks_DoesNotHideLate()
    {
        var harness = LeaseHarness.Create();
        harness.Guard.BlockArm = true;

        var acquireTask = Task.Run(() => harness.Lease.AcquireAsync());
        await harness.Guard.ArmEntered.Task.ConfigureAwait(false);
        var releaseTask = harness.Lease.ReleaseAsync();
        Assert.False(releaseTask.IsCompleted);

        harness.Guard.AllowArm.TrySetResult(true);

        Assert.False(await acquireTask.ConfigureAwait(false));
        Assert.True(await releaseTask.ConfigureAwait(false));
        Assert.Equal(0, harness.Platform.HideCalls);
        Assert.Equal(TaskbarLeaseState.Released, harness.Lease.State);
        Assert.False(harness.Lock.IsOwned);
    }

    [Fact]
    public async Task CanceledReleaseWhileAcquireIsBlocked_RemainsClosedUntilSafeRelease()
    {
        var harness = LeaseHarness.Create();
        harness.Guard.BlockArm = true;

        var acquireTask = Task.Run(() => harness.Lease.AcquireAsync());
        await harness.Guard.ArmEntered.Task.ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        var canceledRelease = harness.Lease.ReleaseAsync(cancellation.Token);
        cancellation.Cancel();

        Assert.False(await canceledRelease.ConfigureAwait(false));

        harness.Guard.AllowArm.TrySetResult(true);
        Assert.False(await acquireTask.ConfigureAwait(false));
        Assert.Equal(0, harness.Platform.HideCalls);

        Assert.False(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        Assert.Equal(0, harness.Platform.HideCalls);

        Assert.True(await harness.Lease.ReleaseAsync().ConfigureAwait(false));
        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
    }

    [Fact]
    public async Task CanceledReleaseWhileReconcileIsBlocked_RemainsClosedUntilSafeRelease()
    {
        var harness = LeaseHarness.Create();
        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        harness.Platform.ReplacePrimary(
            handle: 42,
            processId: 101,
            processStartTicks: 5678,
            monitor: 7,
            visible: true,
            showCommand: NativeMethods.SW_SHOW);
        harness.Journal.BlockOnHidePendingWrite = true;

        var reconcileTask = Task.Run(() => harness.Lease.ReconcileAsync());
        await harness.Journal.HidePendingWriteEntered.Task.ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        var canceledRelease = harness.Lease.ReleaseAsync(cancellation.Token);
        cancellation.Cancel();

        Assert.False(await canceledRelease.ConfigureAwait(false));

        harness.Journal.AllowHidePendingWrite.TrySetResult(true);
        harness.Journal.BlockOnHidePendingWrite = false;
        Assert.False(await reconcileTask.ConfigureAwait(false));
        Assert.Equal(1, harness.Platform.HideCalls);

        Assert.False(await harness.Lease.ReconcileAsync().ConfigureAwait(false));
        Assert.Equal(1, harness.Platform.HideCalls);

        Assert.True(await harness.Lease.ReleaseAsync().ConfigureAwait(false));
        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        Assert.Equal(2, harness.Platform.HideCalls);
    }

    [Fact]
    public async Task OverlappingReleaseRequests_CanceledAndSafeGenerationsRequireSafeCompletion()
    {
        var harness = LeaseHarness.Create();
        harness.Guard.BlockArm = true;

        var acquireTask = Task.Run(() => harness.Lease.AcquireAsync());
        await harness.Guard.ArmEntered.Task.ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        var canceledRelease = harness.Lease.ReleaseAsync(cancellation.Token);
        var safeRelease = harness.Lease.ReleaseAsync();
        cancellation.Cancel();

        Assert.False(await canceledRelease.ConfigureAwait(false));
        Assert.False(safeRelease.IsCompleted);
        Assert.False(await harness.Lease.AcquireAsync().ConfigureAwait(false));

        harness.Guard.AllowArm.TrySetResult(true);
        Assert.False(await acquireTask.ConfigureAwait(false));
        Assert.True(await safeRelease.ConfigureAwait(false));
        Assert.Equal(0, harness.Platform.HideCalls);

        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
    }

    [Fact]
    public async Task SafeReleaseCompletion_CoversNewerCanceledGeneration()
    {
        var harness = LeaseHarness.Create();
        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        harness.Guard.BlockDisarm = true;

        var safeRelease = Task.Run(() => harness.Lease.ReleaseAsync());
        await harness.Guard.DisarmEntered.Task.ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        var canceledRelease = harness.Lease.ReleaseAsync(cancellation.Token);
        cancellation.Cancel();

        Assert.False(await canceledRelease.ConfigureAwait(false));
        Assert.False(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        Assert.False(safeRelease.IsCompleted);

        harness.Guard.AllowDisarm.TrySetResult(true);
        Assert.True(await safeRelease.ConfigureAwait(false));
        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
    }

    [Fact]
    public async Task AcquireCleanup_DoesNotReopenGateWhileReleaseRequestIsOutstanding()
    {
        var harness = LeaseHarness.Create();
        harness.Guard.BlockArm = true;

        var acquireTask = Task.Run(() => harness.Lease.AcquireAsync());
        await harness.Guard.ArmEntered.Task.ConfigureAwait(false);
        var releaseTask = harness.Lease.ReleaseAsync();
        Assert.False(releaseTask.IsCompleted);

        Assert.False(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        Assert.False(await harness.Lease.ReconcileAsync().ConfigureAwait(false));
        Assert.Equal(0, harness.Platform.HideCalls);

        harness.Guard.AllowArm.TrySetResult(true);
        var acquireResult = await acquireTask.ConfigureAwait(false);
        Assert.False(acquireResult);
        Assert.True(await releaseTask.ConfigureAwait(false));

        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
    }

    [Fact]
    public async Task Reconcile_WhenReleaseIsRequestedAtPreHideBarrier_DoesNotHideLate()
    {
        var harness = LeaseHarness.Create();
        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        harness.Platform.ReplacePrimary(
            handle: 42,
            processId: 101,
            processStartTicks: 5678,
            monitor: 7,
            visible: true,
            showCommand: NativeMethods.SW_SHOW);
        harness.Journal.BlockOnHidePendingWrite = true;

        var reconcileTask = Task.Run(() => harness.Lease.ReconcileAsync());
        await harness.Journal.HidePendingWriteEntered.Task.ConfigureAwait(false);
        var releaseTask = harness.Lease.ReleaseAsync();
        Assert.False(releaseTask.IsCompleted);

        harness.Journal.AllowHidePendingWrite.TrySetResult(true);
        harness.Journal.BlockOnHidePendingWrite = false;

        Assert.False(await reconcileTask.ConfigureAwait(false));
        Assert.True(await releaseTask.ConfigureAwait(false));
        Assert.Equal(1, harness.Platform.HideCalls);
        Assert.Equal(TaskbarLeaseState.Released, harness.Lease.State);
    }

    [Fact]
    public async Task Reconcile_AlreadyHiddenPersistsUnchangedBeforeHonoringCancellation()
    {
        var harness = LeaseHarness.Create();
        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        harness.Platform.ReplacePrimary(
            handle: 42,
            processId: 101,
            processStartTicks: 5678,
            monitor: 7,
            visible: true,
            showCommand: NativeMethods.SW_SHOW);
        harness.Journal.BlockOnHidePendingWrite = true;
        using var cancellation = new CancellationTokenSource();
        harness.Platform.AfterHideMutation = cancellation.Cancel;

        var reconcileTask = Task.Run(() => harness.Lease.ReconcileAsync(cancellation.Token));
        await harness.Journal.HidePendingWriteEntered.Task.ConfigureAwait(false);
        harness.Platform.SetWindowVisible(42, visible: false);
        harness.Journal.AllowHidePendingWrite.TrySetResult(true);
        harness.Journal.BlockOnHidePendingWrite = false;

        Assert.False(await reconcileTask.ConfigureAwait(false));
        Assert.Equal(TaskbarLeaseState.Active, harness.Lease.State);
        Assert.Equal(
            TaskbarWindowMutationState.Unchanged,
            harness.Journal.Document!.Windows[1].MutationState);

        Assert.True(await harness.Lease.ReleaseAsync().ConfigureAwait(false));
        Assert.Equal(0, harness.Platform.ShowCalls);
    }

    [Fact]
    public async Task Reconcile_NotHiddenPersistsUnchangedBeforeHonoringCancellation()
    {
        var harness = LeaseHarness.Create();
        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        harness.Platform.ReplacePrimary(
            handle: 42,
            processId: 101,
            processStartTicks: 5678,
            monitor: 7,
            visible: true,
            showCommand: NativeMethods.SW_SHOW);
        harness.Platform.HideChangesVisibility = false;
        using var cancellation = new CancellationTokenSource();
        harness.Platform.AfterHideMutation = cancellation.Cancel;

        Assert.False(
            await harness.Lease.ReconcileAsync(cancellation.Token).ConfigureAwait(false));

        Assert.Equal(TaskbarLeaseState.Active, harness.Lease.State);
        Assert.Equal(
            TaskbarWindowMutationState.Unchanged,
            harness.Journal.Document!.Windows[1].MutationState);
        Assert.True(await harness.Lease.ReleaseAsync().ConfigureAwait(false));
        Assert.Equal(0, harness.Platform.ShowCalls);
    }

    [Fact]
    public async Task Reconcile_WhenUnchangedRewriteFails_RetainsEvidenceAndReleaseRetryNeverShowsKnownExternalHide()
    {
        var harness = LeaseHarness.Create();
        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        harness.Platform.ReplacePrimary(
            handle: 42,
            processId: 101,
            processStartTicks: 5678,
            monitor: 7,
            visible: true,
            showCommand: NativeMethods.SW_SHOW);
        harness.Journal.BlockOnHidePendingWrite = true;
        harness.Platform.AfterHideMutation = () =>
            harness.Journal.ThrowOnWriteCall = harness.Journal.WriteCalls + 1;

        var reconcileTask = Task.Run(() => harness.Lease.ReconcileAsync());
        await harness.Journal.HidePendingWriteEntered.Task.ConfigureAwait(false);
        harness.Platform.SetWindowVisible(42, visible: false);
        harness.Journal.AllowHidePendingWrite.TrySetResult(true);
        harness.Journal.BlockOnHidePendingWrite = false;

        Assert.False(await reconcileTask.ConfigureAwait(false));
        Assert.Equal(TaskbarLeaseState.RecoveryPending, harness.Lease.State);
        Assert.Equal(
            TaskbarWindowMutationState.HidePending,
            harness.Journal.Document!.Windows[1].MutationState);

        Assert.True(await harness.Lease.ReleaseAsync().ConfigureAwait(false));
        Assert.Equal(0, harness.Platform.ShowCalls);
        Assert.False(harness.Journal.Exists);
    }

    [Fact]
    public async Task AcquireAbort_NormalizesHidePendingBeforeDeleteFailureAndReleaseRetryNeverShowsExternalHide()
    {
        var harness = LeaseHarness.Create();
        harness.Journal.BlockOnHidePendingWrite = true;
        harness.Journal.DeleteException = new IOException("Injected cleanup delete failure.");

        var acquireTask = Task.Run(() => harness.Lease.AcquireAsync());
        await harness.Journal.HidePendingWriteEntered.Task.ConfigureAwait(false);
        using var cancellation = new CancellationTokenSource();
        var canceledRelease = harness.Lease.ReleaseAsync(cancellation.Token);
        cancellation.Cancel();

        Assert.False(await canceledRelease.ConfigureAwait(false));

        harness.Journal.AllowHidePendingWrite.TrySetResult(true);
        harness.Journal.BlockOnHidePendingWrite = false;
        Assert.False(await acquireTask.ConfigureAwait(false));

        Assert.Equal(TaskbarLeaseState.RecoveryPending, harness.Lease.State);
        Assert.Equal(
            TaskbarWindowMutationState.Unchanged,
            harness.Journal.Document!.Windows[0].MutationState);
        Assert.Equal(0, harness.Platform.HideCalls);

        harness.Platform.SetWindowVisible(42, visible: false);
        harness.Journal.DeleteException = null;
        harness.Guard.BlockDisarm = true;

        var safeRelease = Task.Run(() => harness.Lease.ReleaseAsync());
        await harness.Guard.DisarmEntered.Task.ConfigureAwait(false);
        Assert.False(await harness.Lease.AcquireAsync().ConfigureAwait(false));

        harness.Guard.AllowDisarm.TrySetResult(true);
        Assert.True(await safeRelease.ConfigureAwait(false));
        Assert.Equal(0, harness.Platform.ShowCalls);
        Assert.False(harness.Journal.Exists);
    }

    [Fact]
    public async Task AcquireCleanup_WhenNormalizationWriteFailsStillDeletesAndReleasesSafely()
    {
        var harness = LeaseHarness.Create();
        harness.Journal.BlockOnHidePendingWrite = true;

        var acquireTask = Task.Run(() => harness.Lease.AcquireAsync());
        await harness.Journal.HidePendingWriteEntered.Task.ConfigureAwait(false);
        harness.Journal.ThrowOnWriteCall = harness.Journal.WriteCalls + 1;
        using var cancellation = new CancellationTokenSource();
        var canceledRelease = harness.Lease.ReleaseAsync(cancellation.Token);
        cancellation.Cancel();

        Assert.False(await canceledRelease.ConfigureAwait(false));

        harness.Journal.AllowHidePendingWrite.TrySetResult(true);
        harness.Journal.BlockOnHidePendingWrite = false;
        Assert.False(await acquireTask.ConfigureAwait(false));

        Assert.Equal(1, harness.Journal.DeleteCalls);
        Assert.Equal(TaskbarLeaseState.Released, harness.Lease.State);
        Assert.False(harness.Journal.Exists);
        Assert.True(harness.Guard.WasDisarmed);
        Assert.False(harness.Lock.IsOwned);
        Assert.Equal(0, harness.Platform.HideCalls);
    }

    [Fact]
    public async Task AcquireVerifiedRollback_NormalizesBeforeDeleteFailureAndReleaseDoesNotShowAgain()
    {
        var harness = LeaseHarness.Create(failAfterHide: true);
        harness.Journal.DeleteException = new IOException("Injected cleanup delete failure.");

        Assert.False(await harness.Lease.AcquireAsync().ConfigureAwait(false));

        Assert.Equal(TaskbarLeaseState.RecoveryPending, harness.Lease.State);
        Assert.Equal(
            TaskbarWindowMutationState.Unchanged,
            harness.Journal.Document!.Windows[0].MutationState);
        Assert.Equal(1, harness.Platform.ShowCalls);
        Assert.True(harness.Platform.IsWindowVisible(42));

        harness.Journal.DeleteException = null;
        Assert.True(await harness.Lease.ReleaseAsync().ConfigureAwait(false));
        Assert.Equal(1, harness.Platform.ShowCalls);
    }

    [Fact]
    public async Task Acquire_CancelAfterLockWithDisposeFailureRetainsHandleForReleaseRetry()
    {
        var harness = LeaseHarness.Create();
        using var cancellation = new CancellationTokenSource();
        harness.Lock.AfterAcquire = cancellation.Cancel;
        harness.Lock.DisposeFailuresRemaining = 1;

        Assert.False(
            await harness.Lease.AcquireAsync(cancellation.Token).ConfigureAwait(false));

        Assert.Equal(TaskbarLeaseState.RecoveryPending, harness.Lease.State);
        Assert.True(harness.Lock.IsOwned);
        Assert.Equal(0, harness.Platform.HideCalls);
        Assert.Equal(0, harness.Guard.ArmCalls);

        Assert.True(await harness.Lease.ReleaseAsync().ConfigureAwait(false));
        Assert.Equal(TaskbarLeaseState.Released, harness.Lease.State);
        Assert.False(harness.Lock.IsOwned);
    }

    [Fact]
    public async Task Release_WhenIdentityInspectionIsUnknown_RetainsRecoveryEvidenceWithoutShowing()
    {
        var harness = LeaseHarness.Create();
        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        harness.Platform.ReturnNullClassName = true;

        Assert.False(await harness.Lease.ReleaseAsync().ConfigureAwait(false));

        Assert.Equal(TaskbarLeaseState.RecoveryPending, harness.Lease.State);
        Assert.True(harness.Journal.Exists);
        Assert.True(harness.Lock.IsOwned);
        Assert.False(harness.Guard.WasDisarmed);
        Assert.Equal(0, harness.Platform.ShowCalls);
    }

    [Fact]
    public async Task Release_WhenIdentityInspectionThrows_RetainsRecoveryEvidenceWithoutShowing()
    {
        var harness = LeaseHarness.Create();
        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        harness.Platform.ThrowOnClassNameInspection = true;

        Assert.False(await harness.Lease.ReleaseAsync().ConfigureAwait(false));

        Assert.Equal(TaskbarLeaseState.RecoveryPending, harness.Lease.State);
        Assert.True(harness.Journal.Exists);
        Assert.True(harness.Lock.IsOwned);
        Assert.False(harness.Guard.WasDisarmed);
        Assert.Equal(0, harness.Platform.ShowCalls);
    }

    [Fact]
    public async Task Release_WhenPidInspectionIsZero_RetainsRecoveryEvidenceWithoutShowing()
    {
        var harness = LeaseHarness.Create();
        Assert.True(await harness.Lease.AcquireAsync().ConfigureAwait(false));
        harness.Platform.ReturnZeroProcessId = true;

        Assert.False(await harness.Lease.ReleaseAsync().ConfigureAwait(false));

        Assert.Equal(TaskbarLeaseState.RecoveryPending, harness.Lease.State);
        Assert.True(harness.Journal.Exists);
        Assert.True(harness.Lock.IsOwned);
        Assert.Equal(0, harness.Platform.ShowCalls);
    }
}
