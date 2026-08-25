using System.Text;
using System.Text.Json;
using MacDock.Core.Services.Taskbar;
using Xunit;

namespace MacDock.Tests;

public sealed class TaskbarStartupGateTests
{
    [Fact]
    public async Task PrepareAsync_RecoversBeforeLoadingSettings()
    {
        var events = new List<string>();
        using var harness = StartupGateHarness.Create(
            events,
            recoverySucceeds: true,
            persistedTaskbarSetting: true);

        var result = await harness.Gate.PrepareAsync();

        Assert.True(result.ChangesAllowed);
        Assert.True(result.Settings.HideWindowsTaskbar);
        Assert.Null(result.Error);
        Assert.Equal(["recover-stale", "settings-load"], events);
        Assert.Equal(1, harness.Recovery.StaleRecoveryCalls);
        Assert.Equal(1, harness.Settings.LoadCalls);
        Assert.Equal(0, harness.Settings.SaveCalls);
    }

    [Fact]
    public async Task PrepareAsync_WhenNoJournalExists_CompletesSuccessfullyWithoutSaving()
    {
        using var harness = StartupGateHarness.Create(recoverySucceeds: true);
        harness.Recovery.StaleRecoveryResult = new(
            Succeeded: true,
            RestoredCount: 0,
            FailedHandles: [],
            Error: null);

        var result = await harness.Gate.PrepareAsync();

        Assert.True(result.ChangesAllowed);
        Assert.False(result.Settings.HideWindowsTaskbar);
        Assert.Equal(0, harness.Settings.SaveCalls);
    }

    [Fact]
    public async Task PrepareAsync_WhenRecoveryFails_PreservesValidPreferenceAndBlocksChanges()
    {
        using var harness = StartupGateHarness.Create(
            recoverySucceeds: false,
            persistedTaskbarSetting: true);

        var result = await harness.Gate.PrepareAsync();

        Assert.False(result.ChangesAllowed);
        Assert.True(result.Settings.HideWindowsTaskbar);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Equal(1, harness.Settings.LoadCalls);
        Assert.Equal(0, harness.Settings.SaveCalls);
        Assert.Equal(harness.Settings.OriginalBytes, harness.Settings.ReadSourceBytes());
    }

    [Fact]
    public async Task PrepareAsync_WhenRecoveryThrows_PreservesSettingsAndFailsClosed()
    {
        using var harness = StartupGateHarness.Create(persistedTaskbarSetting: true);
        harness.Recovery.StaleRecoveryException = new IOException("corrupt residual journal");

        var result = await harness.Gate.PrepareAsync();

        Assert.False(result.ChangesAllowed);
        Assert.True(result.Settings.HideWindowsTaskbar);
        Assert.Contains("corrupt residual journal", result.Error);
        Assert.Equal(1, harness.Settings.LoadCalls);
        Assert.Equal(0, harness.Settings.SaveCalls);
        Assert.Equal(harness.Settings.OriginalBytes, harness.Settings.ReadSourceBytes());
    }

    [Theory]
    [InlineData("old owner is alive")]
    [InlineData("old owner identity is unknown")]
    public async Task PrepareAsync_WhenOldOwnerIsAliveOrUnknown_FailsClosed(string error)
    {
        using var harness = StartupGateHarness.Create(persistedTaskbarSetting: true);
        harness.Recovery.StaleRecoveryResult = new(
            Succeeded: false,
            RestoredCount: 0,
            FailedHandles: [],
            Error: error);

        var result = await harness.Gate.PrepareAsync();

        Assert.False(result.ChangesAllowed);
        Assert.True(result.Settings.HideWindowsTaskbar);
        Assert.Contains(error, result.Error);
        Assert.Equal(0, harness.Settings.SaveCalls);
    }

    [Fact]
    public async Task PrepareAsync_WhenResidualJournalIsUnsupported_PreservesEvidence()
    {
        using var harness = StartupGateHarness.Create(persistedTaskbarSetting: false);
        harness.Recovery.StaleRecoveryException =
            new InvalidDataException("unsupported residual journal schema");

        var originalBytes = harness.Settings.OriginalBytes;
        var result = await harness.Gate.PrepareAsync();

        Assert.False(result.ChangesAllowed);
        Assert.False(result.Settings.HideWindowsTaskbar);
        Assert.Contains("unsupported residual journal schema", result.Error);
        Assert.Equal(originalBytes, harness.Settings.ReadSourceBytes());
        Assert.Equal(0, harness.Settings.SaveCalls);
    }

    [Fact]
    public async Task PrepareAsync_WhenSettingsAreCorrupt_UsesFreshInMemoryDefault()
    {
        using var harness = StartupGateHarness.Create(persistedTaskbarSetting: true);
        harness.Settings.LoadException = new InvalidDataException("corrupt settings");

        var originalBytes = harness.Settings.OriginalBytes;
        var result = await harness.Gate.PrepareAsync();

        Assert.False(result.ChangesAllowed);
        Assert.False(result.Settings.HideWindowsTaskbar);
        Assert.Contains("corrupt settings", result.Error);
        Assert.Equal(1, harness.Settings.LoadCalls);
        Assert.Equal(0, harness.Settings.SaveCalls);
        Assert.Equal(originalBytes, harness.Settings.ReadSourceBytes());
    }

    [Fact]
    public async Task PrepareAsync_WhenCanceled_FailsClosedWithoutRewritingSettings()
    {
        using var harness = StartupGateHarness.Create(persistedTaskbarSetting: true);
        harness.Recovery.StaleRecoveryException = new OperationCanceledException(
            "startup recovery canceled");

        var result = await harness.Gate.PrepareAsync();

        Assert.False(result.ChangesAllowed);
        Assert.True(result.Settings.HideWindowsTaskbar);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Equal(0, harness.Settings.SaveCalls);
        Assert.Equal(harness.Settings.OriginalBytes, harness.Settings.ReadSourceBytes());
    }

    [Fact]
    public async Task PrepareAsync_WhenAlreadyCanceled_FailsClosedWithoutStartingRecovery()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var harness = StartupGateHarness.Create(
            recoverySucceeds: true,
            persistedTaskbarSetting: true);

        var result = await harness.Gate.PrepareAsync(cancellation.Token);

        Assert.False(result.ChangesAllowed);
        Assert.False(result.Settings.HideWindowsTaskbar);
        Assert.Contains("canceled", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, harness.Recovery.StaleRecoveryCalls);
        Assert.Equal(0, harness.Settings.LoadCalls);
    }

    [Fact]
    public async Task PrepareAsync_WhenCanceledAfterRecoveryBeforeSettingsLoad_FailsClosed()
    {
        using var cancellation = new CancellationTokenSource();
        using var harness = StartupGateHarness.Create(
            recoverySucceeds: true,
            persistedTaskbarSetting: true);
        harness.Recovery.AfterStaleRecovery = cancellation.Cancel;

        var result = await harness.Gate.PrepareAsync(cancellation.Token);

        Assert.False(result.ChangesAllowed);
        Assert.True(result.Settings.HideWindowsTaskbar);
        Assert.Contains("canceled", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, harness.Settings.LoadCalls);
    }

    [Fact]
    public async Task PrepareAsync_WhenCanceledDuringSettingsLoad_FailsClosed()
    {
        using var cancellation = new CancellationTokenSource();
        using var harness = StartupGateHarness.Create(
            recoverySucceeds: true,
            persistedTaskbarSetting: true);
        harness.Settings.AfterLoad = cancellation.Cancel;

        var result = await harness.Gate.PrepareAsync(cancellation.Token);

        Assert.False(result.ChangesAllowed);
        Assert.True(result.Settings.HideWindowsTaskbar);
        Assert.Contains("canceled", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, harness.Settings.LoadCalls);
    }

    [Fact]
    public async Task PrepareAsync_ValidKilledWatchdogJournal_IsRecoveredBeforeSettingsLoad()
    {
        var events = new List<string>();
        using var harness = StartupGateHarness.Create(
            events,
            recoverySucceeds: true,
            persistedTaskbarSetting: true);
        harness.Recovery.StaleRecoveryResult = new(
            Succeeded: true,
            RestoredCount: 1,
            FailedHandles: [],
            Error: null);

        var result = await harness.Gate.PrepareAsync();

        Assert.True(result.ChangesAllowed);
        Assert.True(result.Settings.HideWindowsTaskbar);
        Assert.Equal(["recover-stale", "settings-load"], events);
        Assert.Equal(0, harness.Settings.SaveCalls);
    }

    [Fact]
    public async Task PrepareAsync_RealRecovery_CorruptJournalPreservesBytesBeforeLoadingSettings()
    {
        using var harness = RealStartupRecoveryHarness.Create();
        var originalBytes = Encoding.UTF8.GetBytes("{not-json");
        harness.WriteJournalBytes(originalBytes);
        var preservedBeforeLoad = false;
        harness.Settings.BeforeLoad = () =>
        {
            preservedBeforeLoad = File.Exists(harness.JournalPath)
                && originalBytes.SequenceEqual(harness.ReadJournalBytes());
        };

        var result = await harness.Gate.PrepareAsync();

        Assert.False(result.ChangesAllowed);
        Assert.True(result.Settings.HideWindowsTaskbar);
        Assert.True(preservedBeforeLoad);
        Assert.Equal(0, harness.Platform.ShowCalls);
        Assert.Equal(1, harness.Settings.LoadCalls);
        Assert.Equal(originalBytes, harness.ReadJournalBytes());
    }

    [Fact]
    public async Task PrepareAsync_RealRecovery_UnsupportedJournalPreservesBytesBeforeLoadingSettings()
    {
        using var harness = RealStartupRecoveryHarness.Create();
        var originalBytes = JsonSerializer.SerializeToUtf8Bytes(
            LeaseSamples.Active(RealStartupRecoveryHarness.LeaseId, handle: 42)
                with
            {
                SchemaVersion = 999,
            });
        harness.WriteJournalBytes(originalBytes);
        var preservedBeforeLoad = false;
        harness.Settings.BeforeLoad = () =>
        {
            preservedBeforeLoad = File.Exists(harness.JournalPath)
                && originalBytes.SequenceEqual(harness.ReadJournalBytes());
        };

        var result = await harness.Gate.PrepareAsync();

        Assert.False(result.ChangesAllowed);
        Assert.True(preservedBeforeLoad);
        Assert.Equal(0, harness.Platform.ShowCalls);
        Assert.Equal(originalBytes, harness.ReadJournalBytes());
        Assert.Equal(0, harness.Settings.SaveCalls);
    }

    [Theory]
    [InlineData(ProcessIdentityStatus.Alive)]
    [InlineData(ProcessIdentityStatus.Unknown)]
    public async Task PrepareAsync_RealRecovery_AliveOrUnknownOwnerDoesNotRestore(
        ProcessIdentityStatus ownerStatus)
    {
        using var harness = RealStartupRecoveryHarness.Create();
        harness.WriteValidResidualJournal();
        harness.Inspector.Status = ownerStatus;
        var originalBytes = harness.ReadJournalBytes();
        var preservedBeforeLoad = false;
        harness.Settings.BeforeLoad = () =>
        {
            preservedBeforeLoad = File.Exists(harness.JournalPath)
                && originalBytes.SequenceEqual(harness.ReadJournalBytes());
        };

        var result = await harness.Gate.PrepareAsync();

        Assert.False(result.ChangesAllowed);
        Assert.True(preservedBeforeLoad);
        Assert.Equal(0, harness.Platform.ShowCalls);
        Assert.True(File.Exists(harness.JournalPath));
        Assert.Equal(originalBytes, harness.ReadJournalBytes());
        Assert.Equal(1, harness.Inspector.Calls);
        Assert.Equal(1, harness.Settings.LoadCalls);
    }

    [Fact]
    public async Task PrepareAsync_RealRecovery_NotAliveOwnerRestoresAndDeletesBeforeLoadingSettings()
    {
        using var harness = RealStartupRecoveryHarness.Create();
        harness.WriteValidResidualJournal();
        harness.Inspector.Status = ProcessIdentityStatus.NotAlive;
        var recoveredBeforeLoad = false;
        harness.Settings.BeforeLoad = () =>
        {
            recoveredBeforeLoad = !File.Exists(harness.JournalPath)
                && harness.Platform.ShowCalls == 1;
        };

        var result = await harness.Gate.PrepareAsync();

        Assert.True(result.ChangesAllowed);
        Assert.True(recoveredBeforeLoad);
        Assert.Equal(1, harness.Platform.ShowCalls);
        Assert.False(File.Exists(harness.JournalPath));
        Assert.Equal(1, harness.Inspector.Calls);
        Assert.Equal(1, harness.Settings.LoadCalls);
        Assert.Equal(0, harness.Settings.SaveCalls);
    }

}
