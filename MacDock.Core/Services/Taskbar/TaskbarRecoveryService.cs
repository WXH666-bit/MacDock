namespace MacDock.Core.Services.Taskbar;

public sealed class TaskbarRecoveryService : ITaskbarRecoveryService
{
    private readonly TaskbarWindowService _windowService;
    private readonly ITaskbarLeaseJournal _journal;
    private readonly ITaskbarLeaseLock _leaseLock;
    private readonly IProcessInspector _processInspector;
    private readonly TimeSpan _lockTimeout;

    public TaskbarRecoveryService(
        TaskbarWindowService windowService,
        ITaskbarLeaseJournal journal,
        ITaskbarLeaseLock leaseLock,
        IProcessInspector processInspector)
        : this(
            windowService,
            journal,
            leaseLock,
            processInspector,
            TimeSpan.FromSeconds(5))
    {
    }

    public TaskbarRecoveryService(
        TaskbarWindowService windowService,
        ITaskbarLeaseJournal journal,
        ITaskbarLeaseLock leaseLock,
        IProcessInspector processInspector,
        TimeSpan lockTimeout)
    {
        _windowService = windowService ?? throw new ArgumentNullException(nameof(windowService));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _leaseLock = leaseLock ?? throw new ArgumentNullException(nameof(leaseLock));
        _processInspector = processInspector ?? throw new ArgumentNullException(nameof(processInspector));
        if (lockTimeout < TimeSpan.Zero && lockTimeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(lockTimeout));
        _lockTimeout = lockTimeout;
    }

    public Task<TaskbarRecoveryResult> TryRecoverAsync(
        string expectedLeaseId,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidLeaseId(expectedLeaseId))
        {
            return Task.FromResult(
                Failure("The expected taskbar lease ID is invalid."));
        }

        return RecoverAsync(expectedLeaseId, requireStaleOwner: false, cancellationToken);
    }

    public Task<TaskbarRecoveryResult> TryRecoverStaleAsync(
        CancellationToken cancellationToken = default)
        => RecoverAsync(expectedLeaseId: null, requireStaleOwner: true, cancellationToken);

    private async Task<TaskbarRecoveryResult> RecoverAsync(
        string? expectedLeaseId,
        bool requireStaleOwner,
        CancellationToken cancellationToken)
    {
        IAsyncDisposable? lockHandle = null;
        var result = Failure("Recovery did not run.");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            lockHandle = await _leaseLock
                .TryAcquireAsync(_lockTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (lockHandle is null)
            {
                result = Failure("The shared taskbar lease lock is held by another owner.");
            }
            else
            {
                cancellationToken.ThrowIfCancellationRequested();
                TaskbarLeaseDocument? document;
                var canProcessJournal = true;
                try
                {
                    document = _journal.Read();
                }
                catch (Exception exception)
                {
                    result = Failure(
                        $"The taskbar lease journal could not be read: {exception.Message}");
                    document = null;
                    canProcessJournal = false;
                }

                if (canProcessJournal)
                {
                    if (document is null)
                    {
                        result = Success(0);
                    }
                    else if (expectedLeaseId is not null
                        && !string.Equals(document.LeaseId, expectedLeaseId, StringComparison.Ordinal))
                    {
                        result = Failure("The taskbar lease ID did not match the expected owner.");
                    }
                    else
                    {
                        var canProcessDocument = true;
                        try
                        {
                            ValidateDocument(document);
                        }
                        catch (Exception exception)
                        {
                            result = Failure(
                                $"The taskbar lease journal is invalid: {exception.Message}");
                            canProcessDocument = false;
                        }

                        if (canProcessDocument && requireStaleOwner)
                        {
                            try
                            {
                                var ownerStatus = _processInspector.GetIdentityStatus(
                                    document.OwnerProcessId,
                                    document.OwnerProcessStartTimeUtcTicks);
                                if (ownerStatus != ProcessIdentityStatus.NotAlive)
                                {
                                    result = Failure(
                                        "The taskbar lease owner was not confirmed dead.");
                                    canProcessDocument = false;
                                }
                            }
                            catch (Exception exception)
                            {
                                result = Failure(
                                    $"The lease owner identity could not be checked: {exception.Message}");
                                canProcessDocument = false;
                            }
                        }

                        if (canProcessDocument)
                        {
                            result = await RestoreDocumentAsync(document, cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            result = Failure(
                "Taskbar recovery was canceled.",
                result.RestoredCount,
                result.FailedHandles);
        }
        catch (Exception exception)
        {
            result = Failure(
                $"Taskbar recovery failed: {exception.Message}",
                result.RestoredCount,
                result.FailedHandles);
        }
        finally
        {
            if (lockHandle is not null)
            {
                try
                {
                    await lockHandle.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    result = Failure(
                        $"The recovery lock could not be released: {exception.Message}",
                        result.RestoredCount,
                        result.FailedHandles);
                }
            }
        }

        return result;
    }

    private async Task<TaskbarRecoveryResult> RestoreDocumentAsync(
        TaskbarLeaseDocument document,
        CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(false);

        var current = document;
        var restoredCount = 0;
        var failedHandles = new List<long>();

        for (var index = current.Windows.Count - 1; index >= 0; index--)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = current.Windows[index];
                if (snapshot.MutationState is not (TaskbarWindowMutationState.HidePending
                    or TaskbarWindowMutationState.HiddenByLease))
                {
                    continue;
                }

                var outcome = _windowService.TryRestoreDetailed(snapshot);
                if (outcome is TaskbarRestoreOutcome.Failed
                    or TaskbarRestoreOutcome.Indeterminate)
                {
                    failedHandles.Add(snapshot.Handle);
                    continue;
                }

                if (outcome == TaskbarRestoreOutcome.Restored)
                    restoredCount++;

                current = ReplaceWindow(
                    current,
                    snapshot with { MutationState = TaskbarWindowMutationState.Unchanged });
                try
                {
                    _journal.Write(current);
                }
                catch (Exception exception)
                {
                    return Failure(
                        $"The taskbar lease journal could not record recovery progress: {exception.Message}",
                        restoredCount,
                        failedHandles);
                }
            }
            catch (OperationCanceledException)
            {
                return Failure(
                    "Taskbar recovery was canceled.",
                    restoredCount,
                    failedHandles);
            }
            catch (Exception exception)
            {
                return Failure(
                    $"Taskbar recovery failed while processing a window: {exception.Message}",
                    restoredCount,
                    failedHandles);
            }
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (failedHandles.Count > 0)
            {
                return Failure(
                    $"Recovery failed for {failedHandles.Count} taskbar window(s).",
                    restoredCount,
                    failedHandles);
            }

            _journal.Delete();
            return Success(restoredCount);
        }
        catch (OperationCanceledException)
        {
            return Failure(
                "Taskbar recovery was canceled.",
                restoredCount,
                failedHandles);
        }
        catch (Exception exception)
        {
            return Failure(
                $"The taskbar lease journal could not be deleted: {exception.Message}",
                restoredCount,
                failedHandles);
        }
    }

    private static TaskbarLeaseDocument ReplaceWindow(
        TaskbarLeaseDocument document,
        TaskbarWindowSnapshot snapshot)
    {
        var windows = document.Windows.ToArray();
        var index = -1;
        for (var candidate = 0; candidate < windows.Length; candidate++)
        {
            if (windows[candidate].Handle == snapshot.Handle
                && windows[candidate].ProcessId == snapshot.ProcessId
                && windows[candidate].ProcessStartTimeUtcTicks == snapshot.ProcessStartTimeUtcTicks
                && string.Equals(windows[candidate].ClassName, snapshot.ClassName, StringComparison.Ordinal))
            {
                index = candidate;
                break;
            }
        }

        if (index < 0)
            throw new InvalidDataException("The recovery window identity was not found.");

        windows[index] = snapshot;
        return document with
        {
            Windows = windows,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private static void ValidateDocument(TaskbarLeaseDocument document)
    {
        if (document.SchemaVersion != TaskbarLeaseDocument.CurrentSchemaVersion)
            throw new InvalidDataException("The taskbar lease schema version is unsupported.");
        if (!Guid.TryParse(document.LeaseId, out var leaseId) || leaseId == Guid.Empty)
            throw new InvalidDataException("The taskbar lease ID is invalid.");
        if (document.OwnerProcessId <= 0 || document.OwnerProcessStartTimeUtcTicks <= 0)
            throw new InvalidDataException("The taskbar lease owner identity is invalid.");
        if (document.WatchdogProcessId is <= 0)
            throw new InvalidDataException("The watchdog process identity is invalid.");
        if (!Enum.IsDefined(document.Status))
            throw new InvalidDataException("The taskbar lease status is unsupported.");
        if (document.Generation <= 0 || document.UpdatedAtUtc == default)
            throw new InvalidDataException("The taskbar lease metadata is invalid.");
        if (document.Windows is not { Count: > 0 })
            throw new InvalidDataException("The taskbar lease has no window snapshots.");

        var identities = new HashSet<(long Handle, uint ProcessId, long StartTicks, string ClassName)>();
        foreach (var snapshot in document.Windows)
        {
            if (snapshot is null
                || snapshot.Handle <= 0
                || snapshot.ProcessId == 0
                || snapshot.ProcessStartTimeUtcTicks <= 0
                || !string.Equals(snapshot.ClassName, "Shell_TrayWnd", StringComparison.Ordinal)
                || snapshot.MonitorHandle <= 0
                || snapshot.ShowCommand is < 0 or > 11
                || (snapshot.WasVisible && snapshot.ShowCommand == 0)
                || !Enum.IsDefined(snapshot.MutationState)
                || (snapshot.MutationState != TaskbarWindowMutationState.Unchanged
                    && !snapshot.WasVisible)
                || !identities.Add((
                    snapshot.Handle,
                    snapshot.ProcessId,
                    snapshot.ProcessStartTimeUtcTicks,
                    snapshot.ClassName)))
            {
                throw new InvalidDataException("The taskbar lease contains an invalid window snapshot.");
            }
        }
    }

    private static bool IsValidLeaseId(string? leaseId)
        => !string.IsNullOrWhiteSpace(leaseId)
            && Guid.TryParse(leaseId, out var parsed)
            && parsed != Guid.Empty;

    private static TaskbarRecoveryResult Success(int restoredCount)
        => new(
            Succeeded: true,
            RestoredCount: restoredCount,
            FailedHandles: Array.Empty<long>(),
            Error: null);

    private static TaskbarRecoveryResult Failure(
        string error,
        int restoredCount = 0,
        IReadOnlyList<long>? failedHandles = null)
        => new(
            Succeeded: false,
            RestoredCount: restoredCount,
            FailedHandles: failedHandles ?? Array.Empty<long>(),
            Error: error);
}
