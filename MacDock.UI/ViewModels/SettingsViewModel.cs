using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MacDock.Core.Services;
using MacDock.Core.Services.Taskbar;

namespace MacDock.UI.ViewModels;

/// <summary>
/// 设置窗口视图模型：开机自启、任务栏租约开关和下次启动生效的 Shell 偏好。
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly Func<bool, CancellationToken, Task<TaskbarToggleResult>> _setTaskbarEnabled;
    private readonly Func<bool, CancellationToken, Task<ShellPreferenceUpdateResult>>
        _saveTrayTakeoverPreference;
    private readonly Func<bool> _readAutoStart;
    private readonly Action<bool> _writeAutoStart;
    private readonly bool _changesAllowed;
    private readonly bool _initialTrayTakeover;

    private bool _hideWindowsTaskbar;
    private bool _isTaskbarBusy;
    private string? _taskbarError;
    private bool _trayTakeover;
    private bool _isTrayTakeoverBusy;
    private bool _isTrayTakeoverRestartRequired;
    private string? _trayTakeoverError;

    [ObservableProperty]
    private bool _isAutoStart;

    public SettingsViewModel()
        : this(
            initialTaskbarEnabled: false,
            initialTrayTakeover: false,
            changesAllowed: false,
            taskbarError: "Taskbar coordinator is unavailable.",
            setTaskbarEnabled: static (_, _) => Task.FromResult(
                new TaskbarToggleResult(
                    Succeeded: false,
                    Enabled: false,
                    Error: "Taskbar coordination is not configured.")),
            saveTrayTakeoverPreference: static (_, _) => Task.FromResult(
                new ShellPreferenceUpdateResult(
                    Succeeded: false,
                    Enabled: false,
                    Error: "Settings coordination is not configured.")),
            readAutoStart: AutoStartService.IsEnabled,
            writeAutoStart: AutoStartService.SetEnabled)
    {
    }

    public SettingsViewModel(
        bool initialTaskbarEnabled,
        bool initialTrayTakeover,
        bool changesAllowed,
        string? taskbarError,
        Func<bool, CancellationToken, Task<TaskbarToggleResult>> setTaskbarEnabled,
        Func<bool, CancellationToken, Task<ShellPreferenceUpdateResult>>
            saveTrayTakeoverPreference,
        Func<bool> readAutoStart,
        Action<bool> writeAutoStart)
    {
        ArgumentNullException.ThrowIfNull(setTaskbarEnabled);
        ArgumentNullException.ThrowIfNull(saveTrayTakeoverPreference);
        ArgumentNullException.ThrowIfNull(readAutoStart);
        ArgumentNullException.ThrowIfNull(writeAutoStart);

        _setTaskbarEnabled = setTaskbarEnabled;
        _saveTrayTakeoverPreference = saveTrayTakeoverPreference;
        _readAutoStart = readAutoStart;
        _writeAutoStart = writeAutoStart;
        _changesAllowed = changesAllowed;
        _initialTrayTakeover = initialTrayTakeover;
        _hideWindowsTaskbar = changesAllowed && initialTaskbarEnabled;
        _trayTakeover = initialTrayTakeover;
        _taskbarError = taskbarError;
        _isAutoStart = _readAutoStart();

        SetTaskbarVisibilityCommand = new AsyncRelayCommand<bool?>(
            ExecuteTaskbarVisibilityAsync);
        SetTrayTakeoverCommand = new AsyncRelayCommand<bool?>(
            ExecuteTrayTakeoverAsync);
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

    /// <summary>下次启动是否接管原生托盘图标；当前会话不动态应用。</summary>
    public bool TrayTakeover
    {
        get => _trayTakeover;
        private set
        {
            if (!SetProperty(ref _trayTakeover, value))
                return;

            OnPropertyChanged(nameof(CanToggleTrayTakeover));
        }
    }

    public bool IsTrayTakeoverBusy
    {
        get => _isTrayTakeoverBusy;
        private set
        {
            if (!SetProperty(ref _isTrayTakeoverBusy, value))
                return;

            OnPropertyChanged(nameof(CanToggleTrayTakeover));
        }
    }

    /// <summary>
    /// 启动恢复不可用时禁止新增 opt-in；若旧偏好已经开启，仍允许用户将其关闭。
    /// </summary>
    public bool CanToggleTrayTakeover
        => !IsTrayTakeoverBusy && (_changesAllowed || TrayTakeover);

    public bool IsTrayTakeoverRestartRequired
    {
        get => _isTrayTakeoverRestartRequired;
        private set => SetProperty(ref _isTrayTakeoverRestartRequired, value);
    }

    public string? TrayTakeoverError
    {
        get => _trayTakeoverError;
        private set => SetProperty(ref _trayTakeoverError, value);
    }

    public IAsyncRelayCommand<bool?> SetTrayTakeoverCommand { get; }

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

    private async Task ExecuteTrayTakeoverAsync(
        bool? requested,
        CancellationToken cancellationToken)
    {
        if (requested is null || IsTrayTakeoverBusy)
            return;

        if (requested.Value && !_changesAllowed)
        {
            TrayTakeoverError = "本次启动的 Shell 恢复未完成，不能开启托盘接管。";
            OnPropertyChanged(nameof(TrayTakeover));
            return;
        }

        if (!CanToggleTrayTakeover || requested.Value == TrayTakeover)
        {
            OnPropertyChanged(nameof(TrayTakeover));
            return;
        }

        var previous = TrayTakeover;
        IsTrayTakeoverBusy = true;
        TrayTakeoverError = null;

        try
        {
            var result = await _saveTrayTakeoverPreference(
                requested.Value,
                cancellationToken);
            TrayTakeover = result.Enabled;
            OnPropertyChanged(nameof(TrayTakeover));
            IsTrayTakeoverRestartRequired = TrayTakeover != _initialTrayTakeover;

            if (!result.Succeeded)
            {
                TrayTakeoverError = result.Error
                    ?? "托盘接管设置无法保存。";
            }
        }
        catch (OperationCanceledException)
        {
            TrayTakeover = previous;
            OnPropertyChanged(nameof(TrayTakeover));
            TrayTakeoverError = "托盘接管设置已取消。";
        }
        catch (Exception exception)
        {
            TrayTakeover = previous;
            OnPropertyChanged(nameof(TrayTakeover));
            TrayTakeoverError = $"托盘接管设置失败：{exception.Message}";
        }
        finally
        {
            IsTrayTakeoverBusy = false;
        }
    }

    partial void OnIsAutoStartChanged(bool value)
    {
        _writeAutoStart(value);
    }
}
