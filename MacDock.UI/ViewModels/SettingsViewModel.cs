using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MacDock.Core.Services;
using MacDock.Core.Services.Taskbar;

namespace MacDock.UI.ViewModels;

/// <summary>
/// 设置窗口视图模型：开机自启和任务栏租约开关。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly Func<bool, CancellationToken, Task<TaskbarToggleResult>> _setTaskbarEnabled;
    private readonly Func<bool> _readAutoStart;
    private readonly Action<bool> _writeAutoStart;
    private readonly bool _changesAllowed;

    private bool _hideWindowsTaskbar;
    private bool _isTaskbarBusy;
    private string? _taskbarError;

    [ObservableProperty]
    private bool _isAutoStart;

    public SettingsViewModel()
        : this(
            initialTaskbarEnabled: false,
            changesAllowed: false,
            taskbarError: "Taskbar coordinator is unavailable.",
            setTaskbarEnabled: static (_, _) => Task.FromResult(
                new TaskbarToggleResult(
                    Succeeded: false,
                    Enabled: false,
                    Error: "Taskbar coordination is not configured.")),
            readAutoStart: AutoStartService.IsEnabled,
            writeAutoStart: AutoStartService.SetEnabled)
    {
    }

    public SettingsViewModel(
        bool initialTaskbarEnabled,
        bool changesAllowed,
        string? taskbarError,
        Func<bool, CancellationToken, Task<TaskbarToggleResult>> setTaskbarEnabled,
        Func<bool> readAutoStart,
        Action<bool> writeAutoStart)
    {
        ArgumentNullException.ThrowIfNull(setTaskbarEnabled);
        ArgumentNullException.ThrowIfNull(readAutoStart);
        ArgumentNullException.ThrowIfNull(writeAutoStart);

        _setTaskbarEnabled = setTaskbarEnabled;
        _readAutoStart = readAutoStart;
        _writeAutoStart = writeAutoStart;
        _changesAllowed = changesAllowed;
        _hideWindowsTaskbar = changesAllowed && initialTaskbarEnabled;
        _taskbarError = taskbarError;
        _isAutoStart = _readAutoStart();

        SetTaskbarVisibilityCommand = new AsyncRelayCommand<bool?>(
            ExecuteTaskbarVisibilityAsync);
    }

    public bool HideWindowsTaskbar
    {
        get => _hideWindowsTaskbar;
        private set => SetProperty(ref _hideWindowsTaskbar, value);
    }

    public bool IsTaskbarBusy
    {
        get => _isTaskbarBusy;
        private set
        {
            if (!SetProperty(ref _isTaskbarBusy, value))
                return;

            OnPropertyChanged(nameof(CanToggleTaskbar));
        }
    }

    public bool CanToggleTaskbar => _changesAllowed && !IsTaskbarBusy;

    public string? TaskbarError
    {
        get => _taskbarError;
        private set => SetProperty(ref _taskbarError, value);
    }

    public IAsyncRelayCommand<bool?> SetTaskbarVisibilityCommand { get; }

    private async Task ExecuteTaskbarVisibilityAsync(
        bool? requested,
        CancellationToken cancellationToken)
    {
        if (requested is null || !CanToggleTaskbar)
            return;

        var previous = HideWindowsTaskbar;
        IsTaskbarBusy = true;
        TaskbarError = null;

        try
        {
            var result = await _setTaskbarEnabled(
                    requested.Value,
                    cancellationToken);

            // The coordinator's effective state is authoritative even when a
            // later settings write failed after the physical transition.
            HideWindowsTaskbar = result.Enabled;
            OnPropertyChanged(nameof(HideWindowsTaskbar));

            if (!result.Succeeded)
            {
                TaskbarError = result.Error
                    ?? "The taskbar setting could not be applied.";
            }
        }
        catch (OperationCanceledException)
        {
            HideWindowsTaskbar = previous;
            OnPropertyChanged(nameof(HideWindowsTaskbar));
            TaskbarError = "The taskbar setting operation was canceled.";
        }
        catch (Exception exception)
        {
            HideWindowsTaskbar = previous;
            OnPropertyChanged(nameof(HideWindowsTaskbar));
            TaskbarError = $"The taskbar setting operation failed: {exception.Message}";
        }
        finally
        {
            IsTaskbarBusy = false;
        }
    }

    partial void OnIsAutoStartChanged(bool value)
    {
        _writeAutoStart(value);
    }
}
