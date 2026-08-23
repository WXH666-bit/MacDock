using MacDock.Core.Interop;
using MacDock.Core.Services.Taskbar;
using Xunit;

namespace MacDock.Tests;

#pragma warning disable xUnit1030
public sealed class TaskbarRecoveryServiceTests
{
    [Fact]
    public async Task TryRecover_MissingJournalIsIdempotentSuccess()
    {
        var harness = RecoveryHarness.ActiveHiddenLease();
        harness.Journal.Delete();
        harness.Events.Clear();

        var result = await harness.Service.TryRecoverAsync(
                "11111111-1111-1111-1111-111111111111")
            .ConfigureAwait(false);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.RestoredCount);
        Assert.Empty(result.FailedHandles);
        Assert.Equal(0, harness.Platform.ShowCalls);
        Assert.False(harness.Lock.IsOwned);
    }

    [Fact]
    public async Task TryRecover_ExpectedLeaseMismatchDoesNothing()
    {
        var harness = RecoveryHarness.ActiveHiddenLease();

        var result = await harness.Service.TryRecoverAsync(
                "22222222-2222-2222-2222-222222222222")
            .ConfigureAwait(false);

        Assert.False(result.Succeeded);
        Assert.Equal(0, harness.Platform.ShowCalls);
        Assert.Equal(0, harness.Journal.DeleteCalls);
        Assert.True(harness.Journal.Exists);
        Assert.False(harness.Lock.IsOwned);
    }

    [Fact]
    public async Task TryRecover_CalledTwiceDoesNotShowTwice()
    {
        var harness = RecoveryHarness.ActiveHiddenLease();

        var first = await harness.Service.TryRecoverAsync(
                "11111111-1111-1111-1111-111111111111")
            .ConfigureAwait(false);
        var second = await harness.Service.TryRecoverAsync(
                "11111111-1111-1111-1111-111111111111")
            .ConfigureAwait(false);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(1, harness.Platform.ShowCalls);
    }

    [Fact]
    public async Task TryRecoverStale_OnlyNotAliveOwnerMayRecover()
    {
        var harness = RecoveryHarness.ActiveHiddenLease();
        harness.Inspector.Status = ProcessIdentityStatus.Alive;

        var alive = await harness.Service.TryRecoverStaleAsync().ConfigureAwait(false);

        Assert.False(alive.Succeeded);
        Assert.Equal(0, harness.Platform.ShowCalls);
        Assert.True(harness.Journal.Exists);

        harness.Inspector.Status = ProcessIdentityStatus.Unknown;
        var unknown = await harness.Service.TryRecoverStaleAsync().ConfigureAwait(false);

        Assert.False(unknown.Succeeded);
        Assert.Equal(0, harness.Platform.ShowCalls);
        Assert.True(harness.Journal.Exists);
    }

    [Fact]
    public async Task TryRecoverStale_InspectionFailureFailsClosed()
    {
        var harness = RecoveryHarness.ActiveHiddenLease();
        harness.Inspector.Exception = new UnauthorizedAccessException();

        var result = await harness.Service.TryRecoverStaleAsync().ConfigureAwait(false);

        Assert.False(result.Succeeded);
        Assert.Equal(0, harness.Platform.ShowCalls);
        Assert.True(harness.Journal.Exists);
    }

    [Fact]
    public async Task TryRecover_CorruptJournalDoesNotRewriteOrDelete()
    {
        var harness = RecoveryHarness.ActiveHiddenLease();
        harness.Journal.ReadException = new InvalidDataException("corrupt");

        var result = await harness.Service.TryRecoverAsync(
                "11111111-1111-1111-1111-111111111111")
            .ConfigureAwait(false);

        Assert.False(result.Succeeded);
        Assert.Equal(0, harness.Platform.ShowCalls);
        Assert.Equal(0, harness.Journal.WriteCalls);
        Assert.Equal(0, harness.Journal.DeleteCalls);
        Assert.False(harness.Lock.IsOwned);
    }

    [Fact]
    public async Task TryRecover_UnsupportedJournalDoesNotTouchWindows()
    {
        var harness = RecoveryHarness.ActiveHiddenLease();
        harness.Journal.ReadException = new InvalidDataException("unsupported schema");

        var result = await harness.Service.TryRecoverStaleAsync().ConfigureAwait(false);

        Assert.False(result.Succeeded);
        Assert.Equal(0, harness.Inspector.Calls);
        Assert.Equal(0, harness.Platform.ShowCalls);
        Assert.Equal(0, harness.Journal.DeleteCalls);
        Assert.True(harness.Journal.Exists);
    }

    [Fact]
    public async Task TryRecover_InvalidDocumentFromFakeFailsClosed()
    {
        var harness = RecoveryHarness.ActiveHiddenLease();
        harness.Journal.SetDocumentForTest(harness.Journal.Document! with { SchemaVersion = 99 });

        var result = await harness.Service.TryRecoverAsync(
                "11111111-1111-1111-1111-111111111111")
            .ConfigureAwait(false);

        Assert.False(result.Succeeded);
        Assert.Equal(0, harness.Platform.ShowCalls);
        Assert.Equal(0, harness.Journal.DeleteCalls);
        Assert.True(harness.Journal.Exists);
    }

    [Fact]
    public async Task TryRecover_RestoresEligibleWindowsInReverseAndSkipsUnchanged()
    {
        var events = new List<string>();
        var platform = FakeTaskbarPlatform.PrimaryShellTrayWnd(
            handle: 42,
            processId: 10,
            processStartTicks: 20,
            monitor: 30,
            visible: false,
            showCommand: NativeMethods.SW_SHOW,
            events: events);
        platform.AddWindow(
            handle: 43,
            processId: 11,
            processStartTicks: 21,
            monitor: 30,
            visible: false,
            showCommand: NativeMethods.SW_SHOW);
        platform.AddWindow(
            handle: 44,
            processId: 12,
            processStartTicks: 22,
            monitor: 30,
            visible: false,
            showCommand: NativeMethods.SW_SHOW);
        var document = LeaseSamples.Active("11111111-1111-1111-1111-111111111111", 42)
            with
            {
                Windows =
                [
                    new TaskbarWindowSnapshot(
                        42,
                        10,
                        20,
                        "Shell_TrayWnd",
                        30,
                        true,
                        NativeMethods.SW_SHOW,
                        TaskbarWindowMutationState.HiddenByLease),
                    new TaskbarWindowSnapshot(
                        43,
                        11,
                        21,
                        "Shell_TrayWnd",
                        30,
                        true,
                        NativeMethods.SW_SHOW,
                        TaskbarWindowMutationState.HidePending),
                    new TaskbarWindowSnapshot(
                        44,
                        12,
                        22,
                        "Shell_TrayWnd",
                        30,
                        true,
                        NativeMethods.SW_SHOW,
                        TaskbarWindowMutationState.Unchanged),
                ],
            };
        var journal = new FakeTaskbarLeaseJournal(events, document);
        var fileLock = new FakeTaskbarLeaseLock(events);
        var service = new TaskbarRecoveryService(
            new TaskbarWindowService(platform),
            journal,
            fileLock,
            new FakeProcessInspector(),
            TimeSpan.Zero);

        var result = await service.TryRecoverAsync(
                "11111111-1111-1111-1111-111111111111")
            .ConfigureAwait(false);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.RestoredCount);
        Assert.Empty(result.FailedHandles);
        Assert.Equal(2, platform.ShowCalls);
        Assert.True(
            platform.Mutations.IndexOf("ShowWindow:43:5")
            < platform.Mutations.IndexOf("ShowWindow:42:5"));
        Assert.False(journal.Exists);
    }

    [Fact]
    public async Task TryRecover_AlreadyVisibleAndStaleIdentityAreSafeSkips()
    {
        var alreadyVisible = RecoveryHarness.ActiveHiddenLease();
        alreadyVisible.Platform.SetWindowVisible(42, visible: true);
        var visibleResult = await alreadyVisible.Service.TryRecoverAsync(
                "11111111-1111-1111-1111-111111111111")
            .ConfigureAwait(false);

        Assert.True(visibleResult.Succeeded);
        Assert.Equal(0, visibleResult.RestoredCount);
        Assert.Equal(0, alreadyVisible.Platform.ShowCalls);
        Assert.False(alreadyVisible.Journal.Exists);

        var stale = RecoveryHarness.ActiveHiddenLease();
        stale.Platform.SetClassName(42, "ReusedWindow");
        var staleResult = await stale.Service.TryRecoverAsync(
                "11111111-1111-1111-1111-111111111111")
            .ConfigureAwait(false);

        Assert.True(staleResult.Succeeded);
        Assert.Equal(0, staleResult.RestoredCount);
        Assert.Equal(0, stale.Platform.ShowCalls);
        Assert.False(stale.Journal.Exists);
    }

    [Fact]
    public async Task TryRecover_FailedRestoreRetainsOnlyFailedHandleAndCanRetry()
    {
        var harness = RecoveryHarness.ActiveHiddenLease();
        harness.Platform.ShowChangesVisibility = false;

        var first = await harness.Service.TryRecoverAsync(
                "11111111-1111-1111-1111-111111111111")
            .ConfigureAwait(false);

        Assert.False(first.Succeeded);
        Assert.Equal(new[] { 42L }, first.FailedHandles);
        Assert.True(harness.Journal.Exists);

        harness.Platform.ShowChangesVisibility = true;
        var second = await harness.Service.TryRecoverAsync(
                "11111111-1111-1111-1111-111111111111")
            .ConfigureAwait(false);

        Assert.True(second.Succeeded);
        Assert.Equal(1, second.RestoredCount);
        Assert.Empty(second.FailedHandles);
        Assert.Equal(2, harness.Platform.ShowCalls);
        Assert.False(harness.Journal.Exists);
    }

    [Fact]
    public async Task TryRecover_WhenLockIsHeldDoesNotReadOrTouchWindows()
    {
        var harness = RecoveryHarness.ActiveHiddenLease();
        var held = await harness.Lock.TryAcquireAsync(TimeSpan.Zero).ConfigureAwait(false);

        var result = await harness.Service.TryRecoverAsync(
                "11111111-1111-1111-1111-111111111111")
            .ConfigureAwait(false);

        Assert.False(result.Succeeded);
        Assert.Equal(0, harness.Journal.ReadCalls);
        Assert.Equal(0, harness.Platform.ShowCalls);
        Assert.True(harness.Journal.Exists);
        Assert.NotNull(held);

        await held!.DisposeAsync().ConfigureAwait(false);
        Assert.True((await harness.Service.TryRecoverAsync(
                "11111111-1111-1111-1111-111111111111")
            .ConfigureAwait(false)).Succeeded);
    }

    [Fact]
    public async Task TryRecover_CancellationDoesNotTouchWindowsAndReleasesOwnHandle()
    {
        var harness = RecoveryHarness.ActiveHiddenLease();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await harness.Service.TryRecoverAsync(
                "11111111-1111-1111-1111-111111111111",
                cancellation.Token)
            .ConfigureAwait(false);

        Assert.False(result.Succeeded);
        Assert.Equal(0, harness.Platform.ShowCalls);
        Assert.False(harness.Lock.IsOwned);
    }

    [Fact]
    public void ProcessInspector_ClassifiesPidAndStartTimeWithoutTermination()
    {
        Assert.Equal(
            ProcessIdentityStatus.Alive,
            new ProcessInspector(_ => 20).GetIdentityStatus(10, 20));
        Assert.Equal(
            ProcessIdentityStatus.NotAlive,
            new ProcessInspector(_ => 21).GetIdentityStatus(10, 20));
        Assert.Equal(
            ProcessIdentityStatus.NotAlive,
            new ProcessInspector(_ => null).GetIdentityStatus(10, 20));
        Assert.Equal(
            ProcessIdentityStatus.Unknown,
            new ProcessInspector(_ => throw new UnauthorizedAccessException())
                .GetIdentityStatus(10, 20));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("not-a-guid")]
    public async Task TryRecover_RejectsInvalidExpectedLeaseIdBeforeLockOrJournal(string? expectedLeaseId)
    {
        var harness = RecoveryHarness.ActiveHiddenLease();

        var result = await harness.Service.TryRecoverAsync(expectedLeaseId!).ConfigureAwait(false);

        Assert.False(result.Succeeded);
        Assert.Equal(0, harness.Lock.AcquireCalls);
        Assert.Equal(0, harness.Journal.ReadCalls);
        Assert.Equal(0, harness.Platform.ShowCalls);
        Assert.True(harness.Journal.Exists);
    }

    [Fact]
    public async Task TryRecoverStale_NotAliveOwnerRecoversSuccessfully()
    {
        var harness = RecoveryHarness.ActiveHiddenLease();
        harness.Inspector.Status = ProcessIdentityStatus.NotAlive;

        var result = await harness.Service.TryRecoverStaleAsync().ConfigureAwait(false);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.RestoredCount);
        Assert.Empty(result.FailedHandles);
        Assert.Equal(1, harness.Platform.ShowCalls);
        Assert.False(harness.Journal.Exists);
        Assert.False(harness.Lock.IsOwned);
    }

    [Fact]
    public async Task TryRecover_WhenRestoreSucceedsButJournalWriteFailsReportsTruthfulPartialResult()
    {
        var harness = RecoveryHarness.ActiveHiddenLease();
        harness.Journal.ThrowOnWriteCall = 1;

        var result = await harness.Service.TryRecoverAsync(
                "11111111-1111-1111-1111-111111111111")
            .ConfigureAwait(false);

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.RestoredCount);
        Assert.Empty(result.FailedHandles);
        Assert.NotNull(result.Error);
        Assert.True(harness.Journal.Exists);
        Assert.Equal(1, harness.Platform.ShowCalls);
        Assert.False(harness.Lock.IsOwned);
    }

    [Fact]
    public async Task TryRecover_WhenRestoreSucceedsButDeleteFailsReportsTruthfulPartialResult()
    {
        var harness = RecoveryHarness.ActiveHiddenLease();
        harness.Journal.DeleteException = new IOException("delete failed");

        var result = await harness.Service.TryRecoverAsync(
                "11111111-1111-1111-1111-111111111111")
            .ConfigureAwait(false);

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.RestoredCount);
        Assert.Empty(result.FailedHandles);
        Assert.NotNull(result.Error);
        Assert.True(harness.Journal.Exists);
        Assert.Equal(1, harness.Platform.ShowCalls);
        Assert.False(harness.Lock.IsOwned);
    }

    [Fact]
    public async Task TryRecover_WhenLaterWindowFailsRetainsFailedHandleAndPriorRestoreCount()
    {
        var harness = RecoveryHarness.ActiveHiddenLease();
        harness.Platform.AddWindow(
            handle: 43,
            processId: 11,
            processStartTicks: 21,
            monitor: 30,
            visible: false,
            showCommand: NativeMethods.SW_SHOW);
        harness.Journal.SetDocumentForTest(
            harness.Journal.Document! with
            {
                Windows =
                [
                    harness.Journal.Document!.Windows[0],
                    new TaskbarWindowSnapshot(
                        43,
                        11,
                        21,
                        "Shell_TrayWnd",
                        30,
                        true,
                        NativeMethods.SW_SHOW,
                        TaskbarWindowMutationState.HidePending),
                ],
            });
        harness.Platform.ShowChangesVisibility = false;
        harness.Platform.ShowChangesVisibilityByHandle = handle => handle == 43;

        var result = await harness.Service.TryRecoverAsync(
                "11111111-1111-1111-1111-111111111111")
            .ConfigureAwait(false);

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.RestoredCount);
        Assert.Equal(new[] { 42L }, result.FailedHandles);
        Assert.True(harness.Journal.Exists);
        Assert.Equal(2, harness.Platform.ShowCalls);
    }

    [Fact]
    public async Task TryRecover_WhenCanceledAfterFirstRestoreReportsThatRestoreAndRetainsJournal()
    {
        var harness = RecoveryHarness.ActiveHiddenLease();
        harness.Platform.AddWindow(
            handle: 43,
            processId: 11,
            processStartTicks: 21,
            monitor: 30,
            visible: false,
            showCommand: NativeMethods.SW_SHOW);
        harness.Journal.SetDocumentForTest(
            harness.Journal.Document! with
            {
                Windows =
                [
                    harness.Journal.Document!.Windows[0],
                    new TaskbarWindowSnapshot(
                        43,
                        11,
                        21,
                        "Shell_TrayWnd",
                        30,
                        true,
                        NativeMethods.SW_SHOW,
                        TaskbarWindowMutationState.HidePending),
                ],
            });
        using var cancellation = new CancellationTokenSource();
        harness.Platform.AfterShowMutation = cancellation.Cancel;

        var result = await harness.Service.TryRecoverAsync(
                "11111111-1111-1111-1111-111111111111",
                cancellation.Token)
            .ConfigureAwait(false);

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.RestoredCount);
        Assert.NotNull(result.Error);
        Assert.True(harness.Journal.Exists);
        Assert.False(harness.Lock.IsOwned);
    }

    [Fact]
    public async Task TryRecover_WhenLockDisposeFailsOverridesTentativeSuccessButKeepsCount()
    {
        var harness = RecoveryHarness.ActiveHiddenLease();
        harness.Lock.DisposeFailuresRemaining = 1;

        var result = await harness.Service.TryRecoverAsync(
                "11111111-1111-1111-1111-111111111111")
            .ConfigureAwait(false);

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.RestoredCount);
        Assert.Empty(result.FailedHandles);
        Assert.NotNull(result.Error);
        Assert.False(harness.Journal.Exists);
        Assert.True(harness.Lock.IsOwned);
    }

    [Fact]
    public async Task TryRecover_WhenIdentityInspectionIsUnknownFailsClosedAndPreservesJournal()
    {
        var harness = RecoveryHarness.ActiveHiddenLease();
        harness.Platform.ReturnNullClassName = true;

        var result = await harness.Service.TryRecoverAsync(
                "11111111-1111-1111-1111-111111111111")
            .ConfigureAwait(false);

        Assert.False(result.Succeeded);
        Assert.Equal(0, result.RestoredCount);
        Assert.Equal(new[] { 42L }, result.FailedHandles);
        Assert.True(harness.Journal.Exists);
        Assert.Equal(0, harness.Platform.ShowCalls);
        Assert.False(harness.Lock.IsOwned);
    }
}
