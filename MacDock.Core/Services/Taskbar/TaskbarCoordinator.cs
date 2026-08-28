using MacDock.Core.Models;
using MacDock.Core.Services;

namespace MacDock.Core.Services.Taskbar;

public sealed record TaskbarToggleResult(bool Succeeded, bool Enabled, string? Error);

/// <summary>仅持久化、在下次启动生效的 Shell 偏好更新结果。</summary>
public sealed record ShellPreferenceUpdateResult(
    bool Succeeded,
    bool Enabled,
    string? Error);

/// <summary>
/// Serializes taskbar ownership changes without coupling Core to WPF.
/// </summary>
public sealed class TaskbarCoordinator : IAsyncDisposable
{
    private readonly ITaskbarLease _lease;
    private readonly IAppSettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly object _disposeGate = new();
    private readonly bool _changesAllowed;
    private readonly string? _unavailableReason;

    private Task? _disposeTask;
    private bool _isEnabled;
    private bool _settingsWritePending;
    private string? _lastError;
    private int _disposed;
    private int _coordinationClosed;

    public TaskbarCoordinator(
        ITaskbarLease lease,
        IAppSettingsStore settingsStore,
        AppSettings settings,
        bool changesAllowed,
        string? unavailableReason)
    {
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        _settingsStore = settingsStore
            ?? throw new ArgumentNullException(nameof(settingsStore));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _changesAllowed = changesAllowed;
        _unavailableReason = unavailableReason;
    }

    public bool IsEnabled => Volatile.Read(ref _isEnabled);

    public bool ChangesAllowed => _changesAllowed;

    public string? LastError => _lastError;

    public async Task<TaskbarToggleResult> SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var error = "Taskbar setting operation was canceled.";
            _lastError = error;
            return Failure(CurrentEnabled(), error);
        }
        catch (Exception exception)
        {
            var error = $"Taskbar setting operation failed: {exception.Message}";
            _lastError = error;
            return Failure(CurrentEnabled(), error);
        }

        try
        {
            if (!CanCoordinate())
            {
                var error = AvailabilityError();
                _lastError = error;
                return Failure(CurrentEnabled(), error);
            }

            if (enabled)
                return await EnableCoreAsync(cancellationToken).ConfigureAwait(false);

            return await DisableCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var error = "Taskbar setting operation was canceled.";
            _lastError = error;
            return Failure(CurrentEnabled(), error);
        }
        catch (Exception exception)
        {
            var error = $"Taskbar setting operation failed: {exception.Message}";
            _lastError = error;
            return Failure(CurrentEnabled(), error);
        }
        finally
        {
            _serial.Release();
        }
    }

    /// <summary>
    /// 保存托盘接管偏好，但不在当前进程中启动或停止托盘读取器。
    /// 与任务栏租约操作共用串行门，防止两个设置基于同一份内存快照并发写入时互相覆盖。
    /// 启动恢复不可信时禁止新增 opt-in，但仍允许用户关闭已保存的高风险偏好。
    /// </summary>
    public async Task<ShellPreferenceUpdateResult> SaveTrayTakeoverPreferenceAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return PreferenceFailure(
                CurrentTrayTakeoverPreference(),
                "Tray takeover preference save was canceled.");
        }
        catch (Exception exception)
        {
            return PreferenceFailure(
                CurrentTrayTakeoverPreference(),
                $"Tray takeover preference save failed: {exception.Message}");
        }

        try
        {
            var previous = _settings.TrayTakeover;
            if (Volatile.Read(ref _disposed) != 0
                || Volatile.Read(ref _coordinationClosed) != 0)
            {
                return PreferenceFailure(
                    previous,
                    "Settings coordination is shutting down.");
            }

            if (previous == enabled)
                return PreferenceSuccess(enabled);

            if (enabled && !_changesAllowed)
            {
                return PreferenceFailure(
                    previous,
                    _unavailableReason
                        ?? "Shell integration preferences cannot be enabled for this startup.");
            }

            // 任务栏物理状态与持久化偏好尚未收敛时，任何额外保存都有可能
            // 抹掉恢复所需的权威状态。等待用户先完成任务栏恢复或重启。
            if (_settingsWritePending)
            {
                return PreferenceFailure(
                    previous,
                    "Taskbar preference recovery is pending; tray takeover was not changed.");
            }

            _settings.TrayTakeover = enabled;
            try
            {
                // IAppSettingsStore 是同步文件 I/O；即使串行门可立即取得，也不能
                // 在 WPF 命令调用线程直接执行。串行门在整个写入期间保持持有，
                // 因而后台保存仍与任务栏租约设置严格有序。
                await Task.Run(
                        () => _settingsStore.Save(_settings),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                return PreferenceSuccess(enabled);
            }
            catch (Exception exception)
            {
                _settings.TrayTakeover = previous;
                return PreferenceFailure(
                    previous,
                    $"Tray takeover preference could not be saved: {exception.Message}");
            }
        }
        finally
        {
            _serial.Release();
        }
    }

    public async Task<bool> ReconcileAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _lastError = "Taskbar reconciliation was canceled.";
            return false;
        }
        catch (Exception exception)
        {
            _lastError = $"Taskbar reconciliation failed: {exception.Message}";
            return false;
        }

        try
        {
            if (!CanCoordinate())
            {
                _lastError = AvailabilityError();
                return false;
            }

            if (!IsEnabled)
                return false;

            try
            {
                var reconciled = await _lease
                    .ReconcileAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!reconciled)
                    _lastError = "Taskbar reconciliation did not complete.";
                else
                    _lastError = null;
                return reconciled;
            }
            catch (OperationCanceledException)
            {
                _lastError = "Taskbar reconciliation was canceled.";
                return false;
            }
            catch (Exception exception)
            {
                _lastError = $"Taskbar reconciliation failed: {exception.Message}";
                return false;
            }
        }
        finally
        {
            _serial.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource<object?>? completion = null;
        Task disposeTask;

        lock (_disposeGate)
        {
            if (_disposeTask is null)
            {
                completion = new TaskCompletionSource<object?>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                Interlocked.Exchange(ref _coordinationClosed, 1);
                Interlocked.Exchange(ref _disposed, 1);
                _disposeTask = completion.Task;
            }

            disposeTask = _disposeTask;
        }

        if (completion is not null)
            _ = CompleteDisposeAsync(completion);

        return new ValueTask(disposeTask);
    }

    private async Task<TaskbarToggleResult> EnableCoreAsync(
        CancellationToken cancellationToken)
    {
        if (IsEnabled && !_settingsWritePending)
        {
            _lastError = null;
            return Success(true);
        }

        if (IsEnabled && _settingsWritePending)
        {
            _lastError ??= "Taskbar preference recovery is still pending.";
            return Failure(true, _lastError);
        }

        if (_settingsWritePending && !IsEnabled)
        {
            var pendingSave = await RetryPendingDisableSaveAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!pendingSave.Succeeded)
                return pendingSave;
        }

        bool acquired;
        try
        {
            acquired = await _lease.AcquireAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _lastError = "Taskbar acquisition was canceled.";
            return Failure(false, _lastError);
        }
        catch (Exception exception)
        {
            _lastError = $"Taskbar acquisition failed: {exception.Message}";
            return Failure(false, _lastError);
        }

        if (!acquired)
        {
            _lastError = "Taskbar acquisition was not granted.";
            return Failure(false, _lastError);
        }

        _settings.HideWindowsTaskbar = true;
        try
        {
            _settingsStore.Save(_settings);
            _settingsWritePending = false;
            _isEnabled = true;
            _lastError = null;
            return Success(true);
        }
        catch (Exception exception)
        {
            var saveError = $"Taskbar preference could not be saved: {exception.Message}";
            _settings.HideWindowsTaskbar = false;
            var released = await TryRollbackAcquireAsync().ConfigureAwait(false);
            if (released)
            {
                _isEnabled = false;
                _settingsWritePending = false;
                _lastError = saveError;
                return Failure(false, saveError);
            }

            _isEnabled = true;
            _settingsWritePending = true;
            _lastError = $"{saveError} Physical taskbar recovery is pending.";
            return Failure(true, _lastError);
        }
    }

    private async Task<TaskbarToggleResult> DisableCoreAsync(
        CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            if (_settingsWritePending)
                return await RetryPendingDisableSaveAsync(cancellationToken).ConfigureAwait(false);

            _lastError = null;
            return Success(false);
        }

        bool released;
        try
        {
            released = await _lease.ReleaseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _lastError = "Taskbar release was canceled.";
            return Failure(true, _lastError);
        }
        catch (Exception exception)
        {
            _lastError = $"Taskbar release failed: {exception.Message}";
            return Failure(true, _lastError);
        }

        if (!released || _lease.State != TaskbarLeaseState.Released)
        {
            _lastError = "Taskbar release was not verified.";
            return Failure(true, _lastError);
        }

        _isEnabled = false;
        _settings.HideWindowsTaskbar = false;
        return await SaveDisabledPreferenceAsync().ConfigureAwait(false);
    }

    private async Task<TaskbarToggleResult> RetryPendingDisableSaveAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _isEnabled = false;
        _settings.HideWindowsTaskbar = false;
        return await SaveDisabledPreferenceAsync().ConfigureAwait(false);
    }

    private Task<TaskbarToggleResult> SaveDisabledPreferenceAsync()
    {
        try
        {
            _settingsStore.Save(_settings);
            _settingsWritePending = false;
            _lastError = null;
            return Task.FromResult(Success(false));
        }
        catch (Exception exception)
        {
            _settingsWritePending = true;
            _lastError = $"Taskbar preference could not be saved: {exception.Message}";
            return Task.FromResult(Failure(false, _lastError));
        }
    }

    private async Task<bool> TryRollbackAcquireAsync()
    {
        try
        {
            var released = await _lease.ReleaseAsync(CancellationToken.None)
                .ConfigureAwait(false);
            return released && _lease.State == TaskbarLeaseState.Released;
        }
        catch
        {
            return false;
        }
    }

    private async Task CompleteDisposeAsync(
        TaskCompletionSource<object?> completion)
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            completion.TrySetResult(null);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _serial.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            try
            {
                await _lease.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _lastError = $"Taskbar lease disposal failed: {exception.Message}";
            }

            if (_lease.State == TaskbarLeaseState.Released)
            {
                _isEnabled = false;
            }
            else
            {
                _isEnabled = true;
                _lastError ??= "Taskbar lease recovery remains pending.";
            }
        }
        finally
        {
            _serial.Release();
        }
    }

    private bool CanCoordinate()
        => _changesAllowed
            && Volatile.Read(ref _disposed) == 0
            && Volatile.Read(ref _coordinationClosed) == 0;

    private bool CurrentEnabled()
        => Volatile.Read(ref _isEnabled);

    private bool CurrentTrayTakeoverPreference()
        => _settings.TrayTakeover;

    private string AvailabilityError()
        => _changesAllowed
            ? "Taskbar coordination is shutting down."
            : _unavailableReason ?? "Taskbar changes are unavailable for this startup.";

    private static TaskbarToggleResult Success(bool enabled)
        => new(true, enabled, null);

    private static TaskbarToggleResult Failure(bool enabled, string error)
        => new(false, enabled, error);

    private static ShellPreferenceUpdateResult PreferenceSuccess(bool enabled)
        => new(true, enabled, null);

    private static ShellPreferenceUpdateResult PreferenceFailure(
        bool enabled,
        string error)
        => new(false, enabled, error);
}
