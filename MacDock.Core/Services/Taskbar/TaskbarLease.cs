namespace MacDock.Core.Services.Taskbar;

public enum TaskbarLeaseState
{
    Released,
    Acquiring,
    Active,
    Releasing,
    RecoveryPending,
}

public interface ITaskbarLease : IAsyncDisposable
{
    TaskbarLeaseState State { get; }

    Task<bool> AcquireAsync(CancellationToken cancellationToken = default);

    Task<bool> ReconcileAsync(CancellationToken cancellationToken = default);

    Task<bool> ReleaseAsync(CancellationToken cancellationToken = default);
}

public sealed class TaskbarLease : ITaskbarLease
{
    private readonly TaskbarWindowService _windowService;
    private readonly ITaskbarLeaseJournal _journal;
    private readonly ITaskbarLeaseLock _leaseLock;
    private readonly ITaskbarRecoveryGuard _recoveryGuard;
    private readonly int _ownerProcessId;
    private readonly long _ownerProcessStartTimeUtcTicks;
    private readonly TimeSpan _lockTimeout;
    private readonly TimeSpan _guardReadyTimeout;
    private readonly TimeSpan _guardExitTimeout;
    private readonly SemaphoreSlim _serial = new(1, 1);

    private int _state = (int)TaskbarLeaseState.Released;
    private int _coordinationBlocked;
    private int _disposed;
    private int _guardWrapperDisposed;
    private long _nextReleaseRequestGeneration;
    private long _safeReleaseThroughGeneration;
    private int _outstandingReleaseRequests;
    private IAsyncDisposable? _lockHandle;
    private TaskbarRecoveryGuardSession? _guardSession;
    private TaskbarLeaseDocument? _document;
    private string? _leaseId;
    private bool _guardArmed;

    public TaskbarLease(
        TaskbarWindowService windowService,
        ITaskbarLeaseJournal journal,
        ITaskbarLeaseLock leaseLock,
        ITaskbarRecoveryGuard recoveryGuard,
        int ownerProcessId,
        long ownerProcessStartTimeUtcTicks)
        : this(
            windowService,
            journal,
            leaseLock,
            recoveryGuard,
            ownerProcessId,
            ownerProcessStartTimeUtcTicks,
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5))
    {
    }

    public TaskbarLease(
        TaskbarWindowService windowService,
        ITaskbarLeaseJournal journal,
        ITaskbarLeaseLock leaseLock,
        ITaskbarRecoveryGuard recoveryGuard,
        int ownerProcessId,
        long ownerProcessStartTimeUtcTicks,
        TimeSpan lockTimeout,
        TimeSpan guardReadyTimeout,
        TimeSpan guardExitTimeout)
    {
        _windowService = windowService ?? throw new ArgumentNullException(nameof(windowService));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _leaseLock = leaseLock ?? throw new ArgumentNullException(nameof(leaseLock));
        _recoveryGuard = recoveryGuard ?? throw new ArgumentNullException(nameof(recoveryGuard));
        if (ownerProcessId <= 0)
            throw new ArgumentOutOfRangeException(nameof(ownerProcessId));
        if (ownerProcessStartTimeUtcTicks <= 0)
            throw new ArgumentOutOfRangeException(nameof(ownerProcessStartTimeUtcTicks));

        ValidateTimeout(lockTimeout, nameof(lockTimeout));
        ValidateTimeout(guardReadyTimeout, nameof(guardReadyTimeout));
        ValidateTimeout(guardExitTimeout, nameof(guardExitTimeout));

        _ownerProcessId = ownerProcessId;
        _ownerProcessStartTimeUtcTicks = ownerProcessStartTimeUtcTicks;
        _lockTimeout = lockTimeout;
        _guardReadyTimeout = guardReadyTimeout;
        _guardExitTimeout = guardExitTimeout;
    }

    public TaskbarLeaseState State
        => (TaskbarLeaseState)Volatile.Read(ref _state);

    public async Task<bool> AcquireAsync(CancellationToken cancellationToken = default)
    {
        if (!CanCoordinate())
            return false;

        try
        {
            await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        try
        {
            if (!CanCoordinate())
                return false;
            return await AcquireCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _serial.Release();
        }
    }

    public async Task<bool> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        if (!CanCoordinate())
            return false;

        try
        {
            await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        try
        {
            if (!CanCoordinate())
                return false;
            return await ReconcileCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _serial.Release();
        }
    }

    public async Task<bool> ReleaseAsync(CancellationToken cancellationToken = default)
    {
        var requestGeneration = BeginReleaseRequest();
        var safelyReleased = false;

        try
        {
            var entered = false;
            try
            {
                await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
                entered = true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            if (!entered)
                return false;

            try
            {
                safelyReleased = await ReleaseCoreAsync(cancellationToken).ConfigureAwait(false);
                if (safelyReleased)
                    MarkSafeReleaseThroughCurrentGeneration();
                return safelyReleased;
            }
            finally
            {
                _serial.Release();
            }
        }
        finally
        {
            CompleteReleaseRequest(requestGeneration);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        Interlocked.Exchange(ref _coordinationBlocked, 1);

        await _serial.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var safelyReleased = await ReleaseCoreAsync(CancellationToken.None).ConfigureAwait(false);
            if (safelyReleased && State == TaskbarLeaseState.Released)
                await DisposeGuardWrapperOnceAsync().ConfigureAwait(false);
        }
        finally
        {
            _serial.Release();
        }
    }

    private async Task<bool> AcquireCoreAsync(CancellationToken cancellationToken)
    {
        if (State != TaskbarLeaseState.Released || !CanCoordinate())
            return false;

        SetState(TaskbarLeaseState.Acquiring);
        _leaseId = Guid.NewGuid().ToString("D");
        TaskbarWindowSnapshot? originalSnapshot = null;
        var hideAttempted = false;
        TaskbarHideOutcome? hideOutcome = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lockHandle = await _leaseLock
                .TryAcquireAsync(_lockTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (lockHandle is null)
            {
                SetState(TaskbarLeaseState.Released);
                _leaseId = null;
                return false;
            }

            _lockHandle = lockHandle;
            cancellationToken.ThrowIfCancellationRequested();

            // Ownership of an existing journal belongs to recovery.  This read must
            // precede capture and guard arm, and a read failure is fail-closed.
            if (_journal.Read() is not null)
                return await CleanupAcquireResourcesAsync().ConfigureAwait(false);

            if (!CanCoordinate())
                return await CleanupAcquireResourcesAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            originalSnapshot = _windowService.CapturePrimary();
            if (originalSnapshot is null)
                return await CleanupAcquireResourcesAsync().ConfigureAwait(false);

            if (!CanCoordinate())
                return await CleanupAcquireResourcesAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var guardSession = await _recoveryGuard
                .ArmAsync(
                    new TaskbarRecoveryGuardRequest(
                        _leaseId,
                        _ownerProcessId,
                        _ownerProcessStartTimeUtcTicks,
                        _journal.FilePath),
                    _guardReadyTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (guardSession is null)
                return await CleanupAcquireResourcesAsync().ConfigureAwait(false);

            _guardSession = guardSession;
            _guardArmed = true;
            if (!CanCoordinate())
                return await CleanupAcquireResourcesAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var prepared = CreateDocument(
                TaskbarLeaseStatus.Prepared,
                generation: 1,
                [originalSnapshot]);
            _document = prepared;
            _journal.Write(prepared);

            if (!CanCoordinate())
                return await CleanupAcquireResourcesAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (!originalSnapshot.WasVisible)
            {
                var activeWithoutHide = prepared with
                {
                    Status = TaskbarLeaseStatus.Active,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                };
                _document = activeWithoutHide;
                _journal.Write(activeWithoutHide);
                if (!CanCoordinate() || cancellationToken.IsCancellationRequested)
                    return await CleanupAcquireResourcesAsync().ConfigureAwait(false);

                SetState(TaskbarLeaseState.Active);
                return true;
            }

            var hidePending = prepared with
            {
                Windows =
                [originalSnapshot with { MutationState = TaskbarWindowMutationState.HidePending }],
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            _document = hidePending;
            _journal.Write(hidePending);

            if (!CanCoordinate())
                return await CleanupAcquireResourcesAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            // Keep this gate check immediately adjacent to the only hide call.
            if (!CanCoordinate())
                return await CleanupAcquireResourcesAsync().ConfigureAwait(false);
            hideAttempted = true;
            hideOutcome = _windowService.TryHideDetailed(originalSnapshot);
            return hideOutcome switch
            {
                TaskbarHideOutcome.HiddenByLease => await CompleteAcquireAfterHiddenAsync(
                        originalSnapshot with
                        {
                            MutationState = TaskbarWindowMutationState.HiddenByLease,
                        },
                        cancellationToken)
                    .ConfigureAwait(false),
                TaskbarHideOutcome.AlreadyHidden => await CompleteAcquireAfterKnownNoChangeAsync(
                        originalSnapshot,
                        TaskbarHideOutcome.AlreadyHidden,
                        cancellationToken)
                    .ConfigureAwait(false),
                TaskbarHideOutcome.NotHidden => await CompleteAcquireAfterKnownNoChangeAsync(
                        originalSnapshot,
                        TaskbarHideOutcome.NotHidden,
                        cancellationToken)
                    .ConfigureAwait(false),
                _ => SetRecoveryPendingAndReturnFalse(),
            };
        }
        catch
        {
            if (hideAttempted && hideOutcome == TaskbarHideOutcome.HiddenByLease && originalSnapshot is not null)
                return await RollbackFailedAcquireAsync(originalSnapshot).ConfigureAwait(false);

            if (hideAttempted)
            {
                SetState(TaskbarLeaseState.RecoveryPending);
                return false;
            }

            return await CleanupAcquireResourcesAsync().ConfigureAwait(false);
        }
    }

    private async Task<bool> ReconcileCoreAsync(CancellationToken cancellationToken)
    {
        if (State != TaskbarLeaseState.Active || _document is null || !CanCoordinate())
            return false;

        await Task.CompletedTask.ConfigureAwait(false);

        var previousDocument = _document;
        TaskbarWindowSnapshot? captured = null;
        var hideAttempted = false;
        var pendingDocumentPersisted = false;
        TaskbarHideOutcome? hideOutcome = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanCoordinate())
                return false;

            captured = _windowService.CapturePrimary();
            if (captured is null)
                return false;

            if (FindIdentityIndex(previousDocument.Windows, captured) >= 0)
                return true;
            if (!CanCoordinate())
                return false;

            var pendingSnapshot = captured with
            {
                MutationState = captured.WasVisible
                    ? TaskbarWindowMutationState.HidePending
                    : TaskbarWindowMutationState.Unchanged,
            };
            var pendingDocument = previousDocument with
            {
                Generation = checked(previousDocument.Generation + 1),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Windows = previousDocument.Windows.Append(pendingSnapshot).ToArray(),
            };
            _document = pendingDocument;
            pendingDocumentPersisted = true;
            _journal.Write(pendingDocument);

            if (!CanCoordinate())
                return UndoPendingWithoutHide(previousDocument, pendingDocumentPersisted);
            cancellationToken.ThrowIfCancellationRequested();

            if (!captured.WasVisible)
            {
                var active = pendingDocument with
                {
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                };
                _document = active;
                _journal.Write(active);
                return true;
            }

            if (!CanCoordinate())
                return UndoPendingWithoutHide(previousDocument, pendingDocumentPersisted);
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanCoordinate())
                return UndoPendingWithoutHide(previousDocument, pendingDocumentPersisted);

            hideAttempted = true;
            hideOutcome = _windowService.TryHideDetailed(captured);
            return hideOutcome switch
            {
                TaskbarHideOutcome.HiddenByLease => CompleteReconcileAfterHidden(
                    previousDocument,
                    pendingDocument,
                    pendingSnapshot,
                    captured,
                    cancellationToken),
                TaskbarHideOutcome.AlreadyHidden => CompleteReconcileAfterKnownNoChange(
                    pendingDocument,
                    pendingSnapshot,
                    TaskbarHideOutcome.AlreadyHidden,
                    cancellationToken),
                TaskbarHideOutcome.NotHidden => CompleteReconcileAfterKnownNoChange(
                    pendingDocument,
                    pendingSnapshot,
                    TaskbarHideOutcome.NotHidden,
                    cancellationToken),
                _ => SetRecoveryPendingAndReturnFalse(),
            };
        }
        catch
        {
            if (hideAttempted && hideOutcome == TaskbarHideOutcome.HiddenByLease && captured is not null)
                return RollbackReconcile(previousDocument, captured);

            if (hideAttempted && hideOutcome is TaskbarHideOutcome.AlreadyHidden or TaskbarHideOutcome.NotHidden)
            {
                // The in-memory Unchanged snapshot is intentionally retained.  The
                // durable file may still say HidePending; recovery owns that ambiguity.
                SetState(TaskbarLeaseState.RecoveryPending);
                return false;
            }

            if (!hideAttempted)
                return UndoPendingWithoutHide(previousDocument, pendingDocumentPersisted);

            SetState(TaskbarLeaseState.RecoveryPending);
            return false;
        }
    }

    private async Task<bool> ReleaseCoreAsync(CancellationToken cancellationToken)
    {
        if (State == TaskbarLeaseState.Released)
        {
            if (_lockHandle is null && !_guardArmed && _document is null)
            {
                ClearCoordinationGateIfReusable();
                return true;
            }

            return await CleanupResidualResourcesAsync().ConfigureAwait(false);
        }

        if (State == TaskbarLeaseState.Acquiring)
            return false;
        if (State is not (TaskbarLeaseState.Active or TaskbarLeaseState.RecoveryPending))
            return false;
        if (_lockHandle is null)
        {
            SetState(TaskbarLeaseState.RecoveryPending);
            return false;
        }
        if (_document is null)
            return await CleanupResidualResourcesAsync().ConfigureAwait(false);

        SetState(TaskbarLeaseState.Releasing);
        try
        {
            var releasing = _document with
            {
                Status = TaskbarLeaseStatus.Releasing,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            _document = releasing;
            _journal.Write(releasing);

            for (var index = releasing.Windows.Count - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = _document.Windows[index];
                if (snapshot.MutationState is not (TaskbarWindowMutationState.HidePending
                    or TaskbarWindowMutationState.HiddenByLease))
                {
                    continue;
                }

                var restoreOutcome = _windowService.TryRestoreDetailed(snapshot);
                if (restoreOutcome is not (TaskbarRestoreOutcome.Restored
                    or TaskbarRestoreOutcome.AlreadyVisible
                    or TaskbarRestoreOutcome.StaleIdentity))
                {
                    SetState(TaskbarLeaseState.RecoveryPending);
                    return false;
                }

                _document = ReplaceWindow(
                    _document,
                    snapshot with { MutationState = TaskbarWindowMutationState.Unchanged });
                _journal.Write(_document);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _journal.Delete();
            if (_guardArmed)
            {
                await _recoveryGuard
                    .DisarmAsync(_leaseId!, _guardExitTimeout, CancellationToken.None)
                    .ConfigureAwait(false);
                _guardArmed = false;
            }

            await ReleaseLockOnlyAsync().ConfigureAwait(false);
            CompleteReleasedState();
            return true;
        }
        catch (OperationCanceledException)
        {
            RetainDocumentAfterCleanupFailure();
            SetState(TaskbarLeaseState.RecoveryPending);
            return false;
        }
        catch
        {
            RetainDocumentAfterCleanupFailure();
            SetState(TaskbarLeaseState.RecoveryPending);
            return false;
        }
    }

    private async Task<bool> CompleteAcquireAfterHiddenAsync(
        TaskbarWindowSnapshot hiddenSnapshot,
        CancellationToken cancellationToken)
    {
        _document = ReplaceWindow(_document!, hiddenSnapshot) with
        {
            Status = TaskbarLeaseStatus.Active,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        try
        {
            _journal.Write(_document);
            if (cancellationToken.IsCancellationRequested)
                return await RollbackFailedAcquireAsync(hiddenSnapshot).ConfigureAwait(false);

            SetState(TaskbarLeaseState.Active);
            return true;
        }
        catch
        {
            return await RollbackFailedAcquireAsync(hiddenSnapshot).ConfigureAwait(false);
        }
    }

    private async Task<bool> CompleteAcquireAfterKnownNoChangeAsync(
        TaskbarWindowSnapshot unchangedSnapshot,
        TaskbarHideOutcome outcome,
        CancellationToken cancellationToken)
    {
        _document = ReplaceWindow(_document!, unchangedSnapshot with
        {
            MutationState = TaskbarWindowMutationState.Unchanged,
        }) with
        {
            Status = TaskbarLeaseStatus.Active,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        try
        {
            // This write is deliberately before observing cancellation.  It is the
            // durable proof that this lease did not own the hidden state.
            _journal.Write(_document);
        }
        catch
        {
            SetState(TaskbarLeaseState.RecoveryPending);
            return false;
        }

        if (cancellationToken.IsCancellationRequested)
            return await CleanupAcquireResourcesAsync().ConfigureAwait(false);

        if (outcome == TaskbarHideOutcome.AlreadyHidden)
        {
            SetState(TaskbarLeaseState.Active);
            return true;
        }

        return await CleanupAcquireResourcesAsync().ConfigureAwait(false);
    }

    private bool CompleteReconcileAfterHidden(
        TaskbarLeaseDocument previousDocument,
        TaskbarLeaseDocument pendingDocument,
        TaskbarWindowSnapshot pendingSnapshot,
        TaskbarWindowSnapshot captured,
        CancellationToken cancellationToken)
    {
        _document = ReplaceWindow(
            pendingDocument,
            pendingSnapshot with { MutationState = TaskbarWindowMutationState.HiddenByLease });
        try
        {
            _journal.Write(_document);
        }
        catch
        {
            return RollbackReconcile(previousDocument, captured);
        }

        if (cancellationToken.IsCancellationRequested)
            return RollbackReconcile(previousDocument, captured);
        return true;
    }

    private bool CompleteReconcileAfterKnownNoChange(
        TaskbarLeaseDocument pendingDocument,
        TaskbarWindowSnapshot pendingSnapshot,
        TaskbarHideOutcome outcome,
        CancellationToken cancellationToken)
    {
        _document = ReplaceWindow(
            pendingDocument,
            pendingSnapshot with { MutationState = TaskbarWindowMutationState.Unchanged });
        try
        {
            // Persist Unchanged before cancellation or release is observed.  If this
            // fails, retain the in-memory fact and let the durable HidePending remain
            // conservative for a later crash-recovery process.
            _journal.Write(_document);
        }
        catch
        {
            SetState(TaskbarLeaseState.RecoveryPending);
            return false;
        }

        if (cancellationToken.IsCancellationRequested)
            return false;
        return outcome == TaskbarHideOutcome.AlreadyHidden;
    }

    private bool RollbackReconcile(
        TaskbarLeaseDocument previousDocument,
        TaskbarWindowSnapshot captured)
    {
        TaskbarRestoreOutcome restoreOutcome;
        try
        {
            restoreOutcome = _windowService.TryRestoreDetailed(captured);
        }
        catch
        {
            SetState(TaskbarLeaseState.RecoveryPending);
            return false;
        }

        if (restoreOutcome is not (TaskbarRestoreOutcome.Restored
            or TaskbarRestoreOutcome.AlreadyVisible))
        {
            SetState(TaskbarLeaseState.RecoveryPending);
            return false;
        }

        _document = previousDocument;
        try
        {
            _journal.Write(previousDocument);
            SetState(TaskbarLeaseState.Active);
        }
        catch
        {
            SetState(TaskbarLeaseState.RecoveryPending);
        }

        return false;
    }

    private bool UndoPendingWithoutHide(
        TaskbarLeaseDocument previousDocument,
        bool pendingDocumentPersisted)
    {
        _document = previousDocument;
        if (!pendingDocumentPersisted)
            return false;

        try
        {
            _journal.Write(previousDocument);
            SetState(TaskbarLeaseState.Active);
        }
        catch
        {
            SetState(TaskbarLeaseState.RecoveryPending);
        }

        return false;
    }

    private async Task<bool> RollbackFailedAcquireAsync(TaskbarWindowSnapshot snapshot)
    {
        TaskbarRestoreOutcome restoreOutcome;
        try
        {
            restoreOutcome = _windowService.TryRestoreDetailed(snapshot);
        }
        catch
        {
            SetState(TaskbarLeaseState.RecoveryPending);
            return false;
        }

        if (restoreOutcome is not (TaskbarRestoreOutcome.Restored
            or TaskbarRestoreOutcome.AlreadyVisible))
        {
            SetState(TaskbarLeaseState.RecoveryPending);
            return false;
        }

        return await CleanupAcquireResourcesAsync().ConfigureAwait(false);
    }

    private async Task<bool> CleanupAcquireResourcesAsync()
    {
        try
        {
            if (_document is not null)
            {
                NormalizeDocumentForCleanup();
                _journal.Delete();
                _document = null;
            }

            if (_guardArmed)
            {
                await _recoveryGuard
                    .DisarmAsync(_leaseId!, _guardExitTimeout, CancellationToken.None)
                    .ConfigureAwait(false);
                _guardArmed = false;
            }

            await ReleaseLockOnlyAsync().ConfigureAwait(false);
            _guardSession = null;
            _leaseId = null;
            CompleteReleasedState();
            return false;
        }
        catch
        {
            RetainDocumentAfterCleanupFailure();
            SetState(TaskbarLeaseState.RecoveryPending);
            return false;
        }
    }

    private void NormalizeDocumentForCleanup()
    {
        if (_document is null)
            return;

        var changed = false;
        var windows = _document.Windows
            .Select(window =>
            {
                if (window.MutationState is not (TaskbarWindowMutationState.HidePending
                    or TaskbarWindowMutationState.HiddenByLease))
                {
                    return window;
                }

                changed = true;
                return window with
                {
                    MutationState = TaskbarWindowMutationState.Unchanged,
                };
            })
            .ToArray();

        if (!changed)
            return;

        _document = _document with
        {
            Windows = windows,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
        try
        {
            _journal.Write(_document);
        }
        catch
        {
            // Keep the normalized in-memory evidence and continue with cleanup.
            // If the process dies before the rewrite becomes durable, the
            // crash-recovery-first HidePending ambiguity remains intentional.
        }
    }

    private async Task<bool> CleanupResidualResourcesAsync()
    {
        try
        {
            if (_guardArmed)
            {
                await _recoveryGuard
                    .DisarmAsync(_leaseId!, _guardExitTimeout, CancellationToken.None)
                    .ConfigureAwait(false);
                _guardArmed = false;
            }

            await ReleaseLockOnlyAsync().ConfigureAwait(false);
            _document = null;
            _guardSession = null;
            _leaseId = null;
            CompleteReleasedState();
            return true;
        }
        catch
        {
            RetainDocumentAfterCleanupFailure();
            SetState(TaskbarLeaseState.RecoveryPending);
            return false;
        }
    }

    private async Task ReleaseLockOnlyAsync()
    {
        var handle = _lockHandle;
        if (handle is null)
            return;

        await handle.DisposeAsync().ConfigureAwait(false);
        _lockHandle = null;
    }

    private async Task DisposeGuardWrapperOnceAsync()
    {
        if (Interlocked.Exchange(ref _guardWrapperDisposed, 1) != 0)
            return;

        try
        {
            await _recoveryGuard.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // The wrapper is called once only after all lease resources are safe.
        }
    }

    private void RetainDocumentAfterCleanupFailure()
    {
        if (_document is null)
            return;

        try
        {
            _document = _document with
            {
                Status = TaskbarLeaseStatus.Releasing,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            };
            _journal.Write(_document);
        }
        catch
        {
            // Keep the in-memory document and owned resources.  A later retry remains
            // fail-closed even when the journal provider is temporarily unavailable.
        }
    }

    private TaskbarLeaseDocument CreateDocument(
        TaskbarLeaseStatus status,
        long generation,
        IReadOnlyList<TaskbarWindowSnapshot> windows)
        => new(
            SchemaVersion: TaskbarLeaseDocument.CurrentSchemaVersion,
            LeaseId: _leaseId!,
            OwnerProcessId: _ownerProcessId,
            OwnerProcessStartTimeUtcTicks: _ownerProcessStartTimeUtcTicks,
            WatchdogProcessId: _guardSession?.WatchdogProcessId,
            Status: status,
            Generation: generation,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            Windows: windows.ToArray());

    private static int FindIdentityIndex(
        IReadOnlyList<TaskbarWindowSnapshot> windows,
        TaskbarWindowSnapshot candidate)
    {
        for (var index = 0; index < windows.Count; index++)
        {
            var snapshot = windows[index];
            if (snapshot.Handle == candidate.Handle
                && snapshot.ProcessId == candidate.ProcessId
                && snapshot.ProcessStartTimeUtcTicks == candidate.ProcessStartTimeUtcTicks
                && string.Equals(snapshot.ClassName, candidate.ClassName, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static TaskbarLeaseDocument ReplaceWindow(
        TaskbarLeaseDocument document,
        TaskbarWindowSnapshot snapshot)
    {
        var windows = document.Windows.ToArray();
        var index = FindIdentityIndex(windows, snapshot);
        if (index < 0)
            throw new InvalidOperationException("The lease window identity was not found.");

        windows[index] = snapshot;
        return document with
        {
            Windows = windows,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private bool CanCoordinate()
        => Volatile.Read(ref _disposed) == 0
            && Volatile.Read(ref _coordinationBlocked) == 0
            && Volatile.Read(ref _outstandingReleaseRequests) == 0
            && Volatile.Read(ref _safeReleaseThroughGeneration)
                == Volatile.Read(ref _nextReleaseRequestGeneration);

    private bool SetRecoveryPendingAndReturnFalse()
    {
        SetState(TaskbarLeaseState.RecoveryPending);
        return false;
    }

    private void CompleteReleasedState()
    {
        _document = null;
        _guardSession = null;
        _leaseId = null;
        SetState(TaskbarLeaseState.Released);
        ClearCoordinationGateIfReusable();
    }

    private void ClearCoordinationGateIfReusable()
    {
        if (Volatile.Read(ref _disposed) == 0
            && Volatile.Read(ref _outstandingReleaseRequests) == 0
            && Volatile.Read(ref _safeReleaseThroughGeneration)
                == Volatile.Read(ref _nextReleaseRequestGeneration))
        {
            Interlocked.Exchange(ref _coordinationBlocked, 0);
        }
    }

    private long BeginReleaseRequest()
    {
        Interlocked.Increment(ref _outstandingReleaseRequests);
        var generation = Interlocked.Increment(ref _nextReleaseRequestGeneration);
        Interlocked.Exchange(ref _coordinationBlocked, 1);
        return generation;
    }

    private void CompleteReleaseRequest(long generation)
    {
        _ = generation;
        Interlocked.Decrement(ref _outstandingReleaseRequests);
        ClearCoordinationGateIfReusable();
    }

    private void MarkSafeReleaseThroughCurrentGeneration()
    {
        var generation = Volatile.Read(ref _nextReleaseRequestGeneration);
        while (true)
        {
            var safeThrough = Volatile.Read(ref _safeReleaseThroughGeneration);
            if (safeThrough >= generation)
                break;
            if (Interlocked.CompareExchange(
                    ref _safeReleaseThroughGeneration,
                    generation,
                    safeThrough) == safeThrough)
            {
                break;
            }
        }
    }

    private void SetState(TaskbarLeaseState state)
        => Volatile.Write(ref _state, (int)state);

    private static void ValidateTimeout(TimeSpan timeout, string parameterName)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(parameterName);
    }
}
