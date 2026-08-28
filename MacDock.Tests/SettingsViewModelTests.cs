using MacDock.Core.Services.Taskbar;
using Xunit;

namespace MacDock.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public async Task TaskbarToggle_SetsBusyAndDisablesBeforeAwait()
    {
        var completion = new TaskCompletionSource<TaskbarToggleResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var autoStart = new FakeAutoStartSettings();
        var viewModel = Create(
            autoStart: autoStart,
            setTaskbarEnabled: (_, _) => completion.Task);
        var notifications = new List<string?>();
        viewModel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        var commandTask = viewModel.SetTaskbarVisibilityCommand.ExecuteAsync(true);

        Assert.True(viewModel.IsTaskbarBusy);
        Assert.False(viewModel.CanToggleTaskbar);
        Assert.Contains(nameof(viewModel.IsTaskbarBusy), notifications);
        Assert.Contains(nameof(viewModel.CanToggleTaskbar), notifications);

        completion.SetResult(new TaskbarToggleResult(true, true, null));
        await commandTask;

        Assert.False(viewModel.IsTaskbarBusy);
        Assert.True(viewModel.HideWindowsTaskbar);
    }

    [Fact]
    public async Task TaskbarToggle_SuccessUpdatesEffectiveState()
    {
        var viewModel = Create(
            setTaskbarEnabled: (_, _) => Task.FromResult(
                new TaskbarToggleResult(true, true, null)));

        await viewModel.SetTaskbarVisibilityCommand
            .ExecuteAsync(true);

        Assert.True(viewModel.HideWindowsTaskbar);
        Assert.Null(viewModel.TaskbarError);
        Assert.False(viewModel.IsTaskbarBusy);
        Assert.True(viewModel.CanToggleTaskbar);
    }

    [Fact]
    public async Task TaskbarToggle_FailureKeepsPreviousStateAndSurfacesError()
    {
        var viewModel = Create(
            initialTaskbarEnabled: true,
            setTaskbarEnabled: (_, _) => Task.FromResult(
                new TaskbarToggleResult(false, true, "lease release failed")));

        await viewModel.SetTaskbarVisibilityCommand
            .ExecuteAsync(false);

        Assert.True(viewModel.HideWindowsTaskbar);
        Assert.Equal("lease release failed", viewModel.TaskbarError);
        Assert.False(viewModel.IsTaskbarBusy);
    }

    [Fact]
    public async Task TaskbarToggle_PartialPersistenceFailureFollowsAuthoritativeEnabledState()
    {
        var viewModel = Create(
            initialTaskbarEnabled: false,
            setTaskbarEnabled: (_, _) => Task.FromResult(
                new TaskbarToggleResult(false, true, "settings save failed")));

        await viewModel.SetTaskbarVisibilityCommand
            .ExecuteAsync(true);

        Assert.True(viewModel.HideWindowsTaskbar);
        Assert.Equal("settings save failed", viewModel.TaskbarError);
        Assert.False(viewModel.IsTaskbarBusy);
    }

    [Fact]
    public async Task TaskbarToggle_PartialPersistenceFailureCanFollowDisabledAuthoritativeState()
    {
        var viewModel = Create(
            initialTaskbarEnabled: true,
            setTaskbarEnabled: (_, _) => Task.FromResult(
                new TaskbarToggleResult(false, false, "settings save failed")));

        await viewModel.SetTaskbarVisibilityCommand
            .ExecuteAsync(false);

        Assert.False(viewModel.HideWindowsTaskbar);
        Assert.Equal("settings save failed", viewModel.TaskbarError);
        Assert.False(viewModel.IsTaskbarBusy);
    }

    [Fact]
    public async Task TaskbarToggle_CancellationKeepsPreviousStateAndSurfacesError()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var viewModel = Create(
            initialTaskbarEnabled: true,
            setTaskbarEnabled: (_, _) => Task.FromCanceled<TaskbarToggleResult>(
                cancellation.Token));

        await viewModel.SetTaskbarVisibilityCommand
            .ExecuteAsync(false);

        Assert.True(viewModel.HideWindowsTaskbar);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.TaskbarError));
        Assert.False(viewModel.IsTaskbarBusy);
    }

    [Fact]
    public async Task TaskbarToggle_ThrownExceptionKeepsPreviousStateAndSurfacesError()
    {
        var viewModel = Create(
            initialTaskbarEnabled: true,
            setTaskbarEnabled: (_, _) => Task.FromException<TaskbarToggleResult>(
                new IOException("fake taskbar exception")));

        await viewModel.SetTaskbarVisibilityCommand
            .ExecuteAsync(false);

        Assert.True(viewModel.HideWindowsTaskbar);
        Assert.Contains("fake taskbar exception", viewModel.TaskbarError);
        Assert.False(viewModel.IsTaskbarBusy);
    }

    [Fact]
    public async Task BlockedStartup_DisablesToggleAndDoesNotInvokeDelegate()
    {
        var calls = 0;
        var autoStart = new FakeAutoStartSettings();
        var viewModel = Create(
            initialTaskbarEnabled: true,
            changesAllowed: false,
            taskbarError: "startup recovery unavailable",
            autoStart: autoStart,
            setTaskbarEnabled: (_, _) =>
            {
                calls++;
                return Task.FromResult(new TaskbarToggleResult(true, true, null));
            });

        Assert.False(viewModel.HideWindowsTaskbar);
        Assert.False(viewModel.CanToggleTaskbar);
        Assert.Equal("startup recovery unavailable", viewModel.TaskbarError);

        await viewModel.SetTaskbarVisibilityCommand
            .ExecuteAsync(true);

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DuplicateInvocationWhileBusy_IsIgnored()
    {
        var completion = new TaskCompletionSource<TaskbarToggleResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var viewModel = Create(
            setTaskbarEnabled: (_, _) =>
            {
                calls++;
                return completion.Task;
            });

        var first = viewModel.SetTaskbarVisibilityCommand.ExecuteAsync(true);
        var second = viewModel.SetTaskbarVisibilityCommand.ExecuteAsync(false);

        Assert.True(viewModel.IsTaskbarBusy);
        Assert.False(viewModel.CanToggleTaskbar);
        Assert.Equal(1, calls);

        completion.SetResult(new TaskbarToggleResult(true, true, null));
        await Task.WhenAll(first, second);

        Assert.True(viewModel.HideWindowsTaskbar);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task TaskbarToggle_RaisesStateAndErrorNotifications()
    {
        var viewModel = Create(
            setTaskbarEnabled: (_, _) => Task.FromResult(
                new TaskbarToggleResult(false, false, "fake failure")));
        var notifications = new List<string?>();
        viewModel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        await viewModel.SetTaskbarVisibilityCommand
            .ExecuteAsync(true);

        Assert.Contains(nameof(viewModel.IsTaskbarBusy), notifications);
        Assert.Contains(nameof(viewModel.CanToggleTaskbar), notifications);
        Assert.Contains(nameof(viewModel.TaskbarError), notifications);
        Assert.Contains(nameof(viewModel.HideWindowsTaskbar), notifications);
    }

    [Fact]
    public void AutoStart_UsesInjectedReadAndWriteSeams()
    {
        var autoStart = new FakeAutoStartSettings { Enabled = true };
        var viewModel = Create(autoStart: autoStart);

        Assert.True(viewModel.IsAutoStart);
        Assert.Equal(1, autoStart.ReadCalls);

        viewModel.IsAutoStart = false;

        Assert.False(autoStart.Enabled);
        Assert.Equal(1, autoStart.WriteCalls);
    }

    [Fact]
    public async Task TrayTakeover_SaveSuccessUpdatesPreferenceAndRequiresRestart()
    {
        var calls = new List<bool>();
        var viewModel = Create(
            saveTrayTakeoverPreference: (enabled, _) =>
            {
                calls.Add(enabled);
                return Task.FromResult(
                    new ShellPreferenceUpdateResult(true, enabled, null));
            });

        await viewModel.SetTrayTakeoverCommand.ExecuteAsync(true);

        Assert.True(viewModel.TrayTakeover);
        Assert.True(viewModel.IsTrayTakeoverRestartRequired);
        Assert.Null(viewModel.TrayTakeoverError);
        Assert.False(viewModel.IsTrayTakeoverBusy);
        Assert.Equal([true], calls);

        await viewModel.SetTrayTakeoverCommand.ExecuteAsync(false);

        Assert.False(viewModel.TrayTakeover);
        Assert.False(viewModel.IsTrayTakeoverRestartRequired);
        Assert.Equal([true, false], calls);
    }

    [Fact]
    public async Task TrayTakeover_SaveFailureRollsBackAndSurfacesError()
    {
        var viewModel = Create(
            saveTrayTakeoverPreference: static (_, _) => Task.FromResult(
                new ShellPreferenceUpdateResult(
                    false,
                    false,
                    "fake tray preference failure")));

        await viewModel.SetTrayTakeoverCommand.ExecuteAsync(true);

        Assert.False(viewModel.TrayTakeover);
        Assert.False(viewModel.IsTrayTakeoverRestartRequired);
        Assert.Contains("fake tray preference failure", viewModel.TrayTakeoverError);
        Assert.False(viewModel.IsTrayTakeoverBusy);
    }

    [Fact]
    public async Task TrayTakeover_UnavailableStartupBlocksNewOptIn()
    {
        var calls = 0;
        var viewModel = Create(
            changesAllowed: false,
            saveTrayTakeoverPreference: (_, _) =>
            {
                calls++;
                return Task.FromResult(
                    new ShellPreferenceUpdateResult(true, true, null));
            });

        Assert.False(viewModel.CanToggleTrayTakeover);

        await viewModel.SetTrayTakeoverCommand.ExecuteAsync(true);

        Assert.False(viewModel.TrayTakeover);
        Assert.NotNull(viewModel.TrayTakeoverError);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task TrayTakeover_UnavailableStartupStillAllowsOptOut()
    {
        var viewModel = Create(
            initialTrayTakeover: true,
            changesAllowed: false,
            saveTrayTakeoverPreference: static (enabled, _) => Task.FromResult(
                new ShellPreferenceUpdateResult(true, enabled, null)));

        Assert.True(viewModel.TrayTakeover);
        Assert.True(viewModel.CanToggleTrayTakeover);

        await viewModel.SetTrayTakeoverCommand.ExecuteAsync(false);

        Assert.False(viewModel.TrayTakeover);
        Assert.True(viewModel.IsTrayTakeoverRestartRequired);
        Assert.False(viewModel.CanToggleTrayTakeover);
        Assert.Null(viewModel.TrayTakeoverError);
    }

    private static MacDock.UI.ViewModels.SettingsViewModel Create(
        bool initialTaskbarEnabled = false,
        bool initialTrayTakeover = false,
        bool changesAllowed = true,
        string? taskbarError = null,
        FakeAutoStartSettings? autoStart = null,
        Func<bool, CancellationToken, Task<TaskbarToggleResult>>? setTaskbarEnabled = null,
        Func<bool, CancellationToken, Task<ShellPreferenceUpdateResult>>?
            saveTrayTakeoverPreference = null)
    {
        autoStart ??= new FakeAutoStartSettings();
        setTaskbarEnabled ??= static (_, _) => Task.FromResult(
            new TaskbarToggleResult(false, false, "test callback not configured"));
        saveTrayTakeoverPreference ??= static (_, _) => Task.FromResult(
            new ShellPreferenceUpdateResult(
                false,
                false,
                "test callback not configured"));

        return new MacDock.UI.ViewModels.SettingsViewModel(
            initialTaskbarEnabled,
            initialTrayTakeover,
            changesAllowed,
            taskbarError,
            setTaskbarEnabled,
            saveTrayTakeoverPreference,
            autoStart.Read,
            autoStart.Write);
    }

    private sealed class FakeAutoStartSettings
    {
        public bool Enabled { get; set; }

        public int ReadCalls { get; private set; }

        public int WriteCalls { get; private set; }

        public bool Read()
        {
            ReadCalls++;
            return Enabled;
        }

        public void Write(bool enabled)
        {
            WriteCalls++;
            Enabled = enabled;
        }
    }
}
