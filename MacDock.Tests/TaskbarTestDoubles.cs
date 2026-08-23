using MacDock.Core.Interop;
using MacDock.Core.Models;
using MacDock.Core.Services;
using MacDock.Core.Services.Taskbar;

namespace MacDock.Tests;

internal sealed class FakeWindowState
{
    public required long Handle { get; init; }
    public required string ClassName { get; set; }
    public required uint ProcessId { get; init; }
    public required string ProcessName { get; init; }
    public required long ProcessStartTimeUtcTicks { get; set; }
    public required long MonitorHandle { get; set; }
    public required bool Visible { get; set; }
    public required int? ShowCommand { get; set; }
}

internal sealed class FakeTaskbarPlatform : ITaskbarPlatform
{
    private readonly Dictionary<long, FakeWindowState> _windows;

    private FakeTaskbarPlatform(
        FakeWindowState window,
        long primaryMonitor,
        IList<string>? mutations,
        IList<string>? events = null)
    {
        _windows = new Dictionary<long, FakeWindowState>
        {
            [window.Handle] = window,
        };
        PrimaryMonitor = primaryMonitor;
        Mutations = mutations ?? new List<string>();
        Events = events ?? new List<string>();
    }

    public IList<string> Mutations { get; }

    public IList<string> Events { get; }

    public long PrimaryMonitor { get; set; }

    public bool HideChangesVisibility { get; set; } = true;

    public bool ShowChangesVisibility { get; set; } = true;

    public Func<long, bool>? ShowChangesVisibilityByHandle { get; set; }

    public bool DestroyAfterHide { get; set; }

    public bool InvalidateIdentityAfterHide { get; set; }

    public bool DestroyAfterShow { get; set; }

    public bool InvalidateIdentityAfterShow { get; set; }

    public bool ThrowAfterHide { get; set; }

    public bool ThrowAfterShow { get; set; }

    public bool ReturnNullClassName { get; set; }

    public bool ThrowOnClassNameInspection { get; set; }

    public bool ReturnZeroProcessId { get; set; }

    public bool ThrowOnProcessIdInspection { get; set; }

    public bool ReturnNullProcessStartTime { get; set; }

    public bool ThrowOnProcessStartInspection { get; set; }

    public Action? AfterNextVisibilityRead { get; set; }

    public Action? AfterHideMutation { get; set; }

    public Action? AfterShowMutation { get; set; }

    public bool BlockHide { get; set; }

    public TaskCompletionSource<bool> HideEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> AllowHide { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int HideCalls { get; private set; }

    public int ShowCalls { get; private set; }

    public static FakeTaskbarPlatform PrimaryShellTrayWnd(
        long handle,
        uint processId,
        long processStartTicks,
        long monitor,
        bool visible,
        int showCommand,
        IList<string>? mutations = null,
        IList<string>? events = null)
    {
        return new FakeTaskbarPlatform(
            new FakeWindowState
            {
                Handle = handle,
                ClassName = "Shell_TrayWnd",
                ProcessId = processId,
                ProcessName = "explorer",
                ProcessStartTimeUtcTicks = processStartTicks,
                MonitorHandle = monitor,
                Visible = visible,
                ShowCommand = showCommand,
            },
            primaryMonitor: monitor,
            mutations,
            events);
    }

    public static FakeTaskbarPlatform Window(
        long handle,
        string className,
        string processName,
        long monitor)
    {
        return new FakeTaskbarPlatform(
            new FakeWindowState
            {
                Handle = handle,
                ClassName = className,
                ProcessId = 100,
                ProcessName = processName,
                ProcessStartTimeUtcTicks = 1234,
                MonitorHandle = monitor,
                Visible = true,
                ShowCommand = NativeMethods.SW_SHOW,
            },
            primaryMonitor: 7,
            mutations: null);
    }

    public nint FindWindow(string className)
    {
        Events.Add("capture");
        return _windows.Values
            .FirstOrDefault(window => string.Equals(window.ClassName, className, StringComparison.Ordinal))
            is { } match
            ? (nint)match.Handle
            : nint.Zero;
    }

    public bool IsWindow(nint handle)
        => _windows.ContainsKey(handle.ToInt64());

    public string? GetWindowClassName(nint handle)
    {
        if (ThrowOnClassNameInspection)
            throw new InvalidOperationException("Injected class-name inspection failure.");
        if (ReturnNullClassName)
            return null;
        return TryGet(handle, out var state) ? state.ClassName : null;
    }

    public uint GetWindowProcessId(nint handle)
    {
        if (ThrowOnProcessIdInspection)
            throw new InvalidOperationException("Injected process-id inspection failure.");
        if (ReturnZeroProcessId)
            return 0;
        return TryGet(handle, out var state) ? state.ProcessId : 0;
    }

    public string? GetProcessName(uint processId)
        => _windows.Values.FirstOrDefault(window => window.ProcessId == processId)?.ProcessName;

    public long? GetProcessStartTimeUtcTicks(uint processId)
    {
        if (ThrowOnProcessStartInspection)
            throw new InvalidOperationException("Injected process-start inspection failure.");
        if (ReturnNullProcessStartTime)
            return null;
        return _windows.Values
            .Where(window => window.ProcessId == processId)
            .Select(window => (long?)window.ProcessStartTimeUtcTicks)
            .FirstOrDefault();
    }

    public nint GetWindowMonitor(nint handle)
        => TryGet(handle, out var state) ? (nint)state.MonitorHandle : nint.Zero;

    public nint GetPrimaryMonitor()
        => (nint)PrimaryMonitor;

    public bool IsWindowVisible(nint handle)
    {
        var visible = TryGet(handle, out var state) && state.Visible;
        var callback = AfterNextVisibilityRead;
        AfterNextVisibilityRead = null;
        callback?.Invoke();
        return visible;
    }

    public int? GetWindowShowCommand(nint handle)
        => TryGet(handle, out var state) ? state.ShowCommand : null;

    public bool SetWindowShowState(nint handle, int command)
    {
        if (!TryGet(handle, out var state))
            return false;

        var wasVisible = state.Visible;
        Mutations.Add($"ShowWindow:{handle.ToInt64()}:{command}");

        if (command == NativeMethods.SW_HIDE)
        {
            HideCalls++;
            Events.Add("hide");
            if (BlockHide)
            {
                HideEntered.TrySetResult(true);
                AllowHide.Task.GetAwaiter().GetResult();
            }
        }
        else
        {
            ShowCalls++;
            Events.Add("show");
        }

        if (command == NativeMethods.SW_HIDE && HideChangesVisibility)
            state.Visible = false;
        else if (command != NativeMethods.SW_HIDE
            && (ShowChangesVisibilityByHandle?.Invoke(handle.ToInt64()) ?? ShowChangesVisibility))
            state.Visible = true;

        if (command == NativeMethods.SW_HIDE)
            AfterHideMutation?.Invoke();
        else
            AfterShowMutation?.Invoke();

        if (command == NativeMethods.SW_HIDE && DestroyAfterHide)
        {
            Mutations.Add($"Destroy:{handle.ToInt64()}");
            _windows.Remove(handle.ToInt64());
        }
        else if (command == NativeMethods.SW_HIDE && InvalidateIdentityAfterHide)
        {
            state.ClassName = "ReusedWindow";
            state.ProcessStartTimeUtcTicks++;
            Mutations.Add($"IdentityInvalidated:{handle.ToInt64()}");
        }

        if (command == NativeMethods.SW_HIDE && ThrowAfterHide)
            throw new InvalidOperationException("Injected hide failure after the platform mutation.");

        if (command != NativeMethods.SW_HIDE && DestroyAfterShow)
        {
            Mutations.Add($"Destroy:{handle.ToInt64()}");
            _windows.Remove(handle.ToInt64());
        }
        else if (command != NativeMethods.SW_HIDE && InvalidateIdentityAfterShow)
        {
            state.ClassName = "ReusedWindow";
            state.ProcessStartTimeUtcTicks++;
            Mutations.Add($"IdentityInvalidated:{handle.ToInt64()}");
        }

        if (command != NativeMethods.SW_HIDE && ThrowAfterShow)
            throw new InvalidOperationException("Injected show failure after the platform mutation.");

        return wasVisible;
    }

    public bool IsWindowVisible(long handle)
        => IsWindowVisible((nint)handle);

    public void SetWindowVisible(long handle, bool visible)
    {
        if (!TryGet((nint)handle, out var state))
            throw new InvalidOperationException($"Unknown fake window {handle}.");

        state.Visible = visible;
        Mutations.Add($"Visible:{handle}:{visible}");
    }

    public void SetMonitor(long handle, long monitor)
    {
        if (!TryGet((nint)handle, out var state))
            throw new InvalidOperationException($"Unknown fake window {handle}.");

        state.MonitorHandle = monitor;
        Mutations.Add($"Monitor:{handle}:{monitor}");
    }

    public void SetClassName(long handle, string className)
    {
        if (!TryGet((nint)handle, out var state))
            throw new InvalidOperationException($"Unknown fake window {handle}.");

        state.ClassName = className;
        Mutations.Add($"Class:{handle}:{className}");
    }

    public void SetShowCommand(long handle, int? showCommand)
    {
        if (!TryGet((nint)handle, out var state))
            throw new InvalidOperationException($"Unknown fake window {handle}.");

        state.ShowCommand = showCommand;
        Mutations.Add($"ShowCommand:{handle}:{showCommand?.ToString() ?? "null"}");
    }

    public void ReplacePrimary(
        long handle,
        uint processId,
        long processStartTicks,
        long monitor,
        bool visible,
        int showCommand)
    {
        _windows.Clear();
        _windows[handle] = new FakeWindowState
        {
            Handle = handle,
            ClassName = "Shell_TrayWnd",
            ProcessId = processId,
            ProcessName = "explorer",
            ProcessStartTimeUtcTicks = processStartTicks,
            MonitorHandle = monitor,
            Visible = visible,
            ShowCommand = showCommand,
        };
        PrimaryMonitor = monitor;
        Mutations.Add($"Replace:{handle}:{processId}:{processStartTicks}");
    }

    public void AddWindow(
        long handle,
        uint processId,
        long processStartTicks,
        long monitor,
        bool visible,
        int showCommand,
        string className = "Shell_TrayWnd",
        string processName = "explorer")
    {
        _windows[handle] = new FakeWindowState
        {
            Handle = handle,
            ClassName = className,
            ProcessId = processId,
            ProcessName = processName,
            ProcessStartTimeUtcTicks = processStartTicks,
            MonitorHandle = monitor,
            Visible = visible,
            ShowCommand = showCommand,
        };
    }

    private bool TryGet(nint handle, out FakeWindowState state)
        => _windows.TryGetValue(handle.ToInt64(), out state!);
}

internal sealed class TaskbarWindowHarness
{
    private TaskbarWindowHarness(
        TaskbarWindowService service,
        FakeTaskbarPlatform platform,
        TaskbarWindowSnapshot snapshot)
    {
        Service = service;
        Platform = platform;
        Snapshot = snapshot;
    }

    public TaskbarWindowService Service { get; }

    public FakeTaskbarPlatform Platform { get; }

    public TaskbarWindowSnapshot Snapshot { get; }

    public static TaskbarWindowHarness HiddenPrimaryThenMoved(long handle)
    {
        var platform = FakeTaskbarPlatform.PrimaryShellTrayWnd(
            handle,
            processId: 100,
            processStartTicks: 1234,
            monitor: 7,
            visible: true,
            showCommand: NativeMethods.SW_SHOW);
        var service = new TaskbarWindowService(platform);
        var snapshot = service.CapturePrimary()
            ?? throw new InvalidOperationException("The fake primary taskbar should be capturable.");

        AssertHideWasApplied(platform, handle);
        platform.SetMonitor(handle, 8);

        return new TaskbarWindowHarness(service, platform, snapshot);
    }

    private static void AssertHideWasApplied(FakeTaskbarPlatform platform, long handle)
    {
        if (!platform.SetWindowShowState((nint)handle, NativeMethods.SW_HIDE)
            || platform.IsWindowVisible(handle))
        {
            throw new InvalidOperationException("The fake primary taskbar hide did not apply.");
        }
    }
}

internal static class LeaseSamples
{
    public static TaskbarLeaseDocument Active(string leaseId, long handle)
        => new(
            SchemaVersion: TaskbarLeaseDocument.CurrentSchemaVersion,
            LeaseId: leaseId,
            OwnerProcessId: 10,
            OwnerProcessStartTimeUtcTicks: 20,
            WatchdogProcessId: null,
            Status: TaskbarLeaseStatus.Active,
            Generation: 1,
            UpdatedAtUtc: new DateTimeOffset(2026, 8, 22, 0, 0, 0, TimeSpan.Zero),
            Windows:
            [
                new TaskbarWindowSnapshot(
                    Handle: handle,
                    ProcessId: 10,
                    ProcessStartTimeUtcTicks: 20,
                    ClassName: "Shell_TrayWnd",
                    MonitorHandle: 30,
                    WasVisible: true,
                    ShowCommand: NativeMethods.SW_SHOW,
                    MutationState: TaskbarWindowMutationState.HiddenByLease),
            ]);
}

internal sealed class FakeTaskbarLeaseJournal : ITaskbarLeaseJournal
{
    private TaskbarLeaseDocument? _document;

    public FakeTaskbarLeaseJournal(
        IList<string>? events = null,
        TaskbarLeaseDocument? document = null)
    {
        Events = events ?? new List<string>();
        _document = document;
    }

    public IList<string> Events { get; }

    public string FilePath { get; } = "fake-taskbar-lease.json";

    public bool Exists => _document is not null;

    public TaskbarLeaseDocument? Document => Clone(_document);

    public Exception? ReadException { get; set; }

    public Exception? WriteException { get; set; }

    public Exception? DeleteException { get; set; }

    public bool BlockOnHidePendingWrite { get; set; }

    public TaskCompletionSource<bool> HidePendingWriteEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> AllowHidePendingWrite { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int? ThrowOnWriteCall { get; set; }

    public TaskbarWindowMutationState? ThrowOnWriteMutationState { get; set; }

    public TaskbarLeaseStatus? ThrowOnWriteStatus { get; set; }

    public int ThrowOnWriteStatusCount { get; set; } = 1;

    public int ReadCalls { get; private set; }

    public int WriteCalls { get; private set; }

    public int DeleteCalls { get; private set; }

    public void SetDocumentForTest(TaskbarLeaseDocument document)
        => _document = Clone(document);

    public TaskbarLeaseDocument? Read()
    {
        ReadCalls++;
        if (ReadException is not null)
            throw ReadException;

        Events.Add("journal-read");
        return Clone(_document);
    }

    public void Write(TaskbarLeaseDocument document)
    {
        WriteCalls++;
        Events.Add(EventFor(document));

        if (BlockOnHidePendingWrite
            && document.Windows.Any(window => window.MutationState == TaskbarWindowMutationState.HidePending))
        {
            HidePendingWriteEntered.TrySetResult(true);
            AllowHidePendingWrite.Task.GetAwaiter().GetResult();
        }

        if (WriteException is not null)
            throw WriteException;

        if (ThrowOnWriteCall == WriteCalls
            || (ThrowOnWriteMutationState is { } mutationState
                && document.Windows.Any(window => window.MutationState == mutationState)))
        {
            ThrowOnWriteCall = null;
            ThrowOnWriteMutationState = null;
            throw new IOException("Injected journal write failure.");
        }

        if (ThrowOnWriteStatus == document.Status && ThrowOnWriteStatusCount > 0)
        {
            ThrowOnWriteStatusCount--;
            throw new IOException("Injected journal write failure.");
        }

        _document = Clone(document);
    }

    public void Delete()
    {
        DeleteCalls++;
        Events.Add("journal-delete");
        if (DeleteException is not null)
            throw DeleteException;

        _document = null;
    }

    private static string EventFor(TaskbarLeaseDocument document)
    {
        if (document.Windows.Any(window =>
                window.MutationState == TaskbarWindowMutationState.HidePending))
        {
            return "journal-hide-pending";
        }

        if (document.Status == TaskbarLeaseStatus.Prepared)
            return "journal-prepared";
        if (document.Status == TaskbarLeaseStatus.Releasing)
            return "journal-releasing";

        return "journal-active";
    }

    private static TaskbarLeaseDocument? Clone(TaskbarLeaseDocument? document)
        => document is null
            ? null
            : document with
            {
                Windows = document.Windows.ToArray(),
            };
}

internal sealed class FakeTaskbarLeaseLock : ITaskbarLeaseLock
{
    private int _owned;

    public FakeTaskbarLeaseLock(IList<string>? events = null)
    {
        Events = events ?? new List<string>();
    }

    public IList<string> Events { get; }

    public bool FailAcquire { get; set; }

    public int AcquireCalls { get; private set; }

    public int DisposeCalls { get; private set; }

    public Exception? DisposeException { get; set; }

    public int DisposeFailuresRemaining { get; set; }

    public Action? AfterAcquire { get; set; }

    public bool IsOwned => Volatile.Read(ref _owned) != 0;

    public async Task<IAsyncDisposable?> TryAcquireAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AcquireCalls++;

        if (FailAcquire || Interlocked.CompareExchange(ref _owned, 1, 0) != 0)
            return null;

        Events.Add("lease-lock");
        AfterAcquire?.Invoke();
        await Task.CompletedTask.ConfigureAwait(false);
        return new Handle(this);
    }

    private sealed class Handle : IAsyncDisposable
    {
        private FakeTaskbarLeaseLock? _owner;

        public Handle(FakeTaskbarLeaseLock owner)
        {
            _owner = owner;
        }

        public async ValueTask DisposeAsync()
        {
            var owner = Volatile.Read(ref _owner);
            if (owner is not null)
            {
                owner.DisposeCalls++;
                owner.Events.Add("lock-release");
                if (owner.DisposeException is not null
                    || owner.DisposeFailuresRemaining > 0)
                {
                    if (owner.DisposeFailuresRemaining > 0)
                        owner.DisposeFailuresRemaining--;
                    throw owner.DisposeException
                        ?? new IOException("Injected lock-handle dispose failure.");
                }

                if (Interlocked.CompareExchange(ref _owner, null, owner) == owner)
                    Interlocked.Exchange(ref owner._owned, 0);
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }
    }
}

internal sealed class FakeTaskbarRecoveryGuard : ITaskbarRecoveryGuard
{
    public FakeTaskbarRecoveryGuard(IList<string>? events = null)
    {
        Events = events ?? new List<string>();
    }

    public IList<string> Events { get; }

    public bool ArmSucceeds { get; set; } = true;

    public bool RejectArmAfterDispose { get; set; }

    public bool DisarmSucceeds { get; set; } = true;

    public Exception? ArmException { get; set; }

    public Exception? DisarmException { get; set; }

    public bool BlockDisarm { get; set; }

    public TaskCompletionSource<bool> DisarmEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> AllowDisarm { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool BlockArm { get; set; }

    public TaskCompletionSource<bool> ArmEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> AllowArm { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int ArmCalls { get; private set; }

    public int DisarmCalls { get; private set; }

    public bool WasDisarmed { get; private set; }

    public bool WasDisposed { get; private set; }

    public int DisposeCalls { get; private set; }

    public TaskbarRecoveryGuardRequest? LastRequest { get; private set; }

    public Task<TaskbarRecoveryGuardSession?> ArmAsync(
        TaskbarRecoveryGuardRequest request,
        TimeSpan readyTimeout,
        CancellationToken cancellationToken)
    {
        ArmCalls++;
        LastRequest = request;
        Events.Add("guard-arm");
        cancellationToken.ThrowIfCancellationRequested();

        if (RejectArmAfterDispose && WasDisposed)
            return Task.FromException<TaskbarRecoveryGuardSession?>(
                new ObjectDisposedException(nameof(FakeTaskbarRecoveryGuard)));

        if (BlockArm)
        {
            ArmEntered.TrySetResult(true);
            AllowArm.Task.GetAwaiter().GetResult();
        }

        if (ArmException is not null)
            return Task.FromException<TaskbarRecoveryGuardSession?>(ArmException);

        return Task.FromResult(
            ArmSucceeds
                ? new TaskbarRecoveryGuardSession(WatchdogProcessId: 30)
                : null);
    }

    public Task DisarmAsync(
        string leaseId,
        TimeSpan exitTimeout,
        CancellationToken cancellationToken)
    {
        DisarmCalls++;
        Events.Add("guard-disarm");
        cancellationToken.ThrowIfCancellationRequested();
        if (BlockDisarm)
        {
            DisarmEntered.TrySetResult(true);
            AllowDisarm.Task.GetAwaiter().GetResult();
        }
        if (DisarmException is not null)
            return Task.FromException(DisarmException);
        if (!DisarmSucceeds)
            return Task.FromException(new IOException("Injected guard disarm failure."));

        WasDisarmed = true;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DisposeCalls++;
        WasDisposed = true;
        Events.Add("guard-dispose");
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeProcessInspector : IProcessInspector
{
    public ProcessIdentityStatus Status { get; set; } = ProcessIdentityStatus.NotAlive;

    public Exception? Exception { get; set; }

    public int Calls { get; private set; }

    public ProcessIdentityStatus GetIdentityStatus(
        int processId,
        long processStartTimeUtcTicks)
    {
        Calls++;
        if (Exception is not null)
            throw Exception;
        return Status;
    }
}

internal sealed class LeaseHarness
{
    private LeaseHarness(
        TaskbarLease lease,
        FakeTaskbarPlatform platform,
        FakeTaskbarLeaseJournal journal,
        FakeTaskbarLeaseLock fileLock,
        FakeTaskbarRecoveryGuard guard,
        IList<string> events)
    {
        Lease = lease;
        Platform = platform;
        Journal = journal;
        Lock = fileLock;
        Guard = guard;
        Events = events;
    }

    public TaskbarLease Lease { get; }

    public FakeTaskbarPlatform Platform { get; }

    public FakeTaskbarLeaseJournal Journal { get; }

    public FakeTaskbarLeaseLock Lock { get; }

    public FakeTaskbarRecoveryGuard Guard { get; }

    public IList<string> Events { get; }

    public static LeaseHarness Create(
        IList<string>? events = null,
        bool hideChangesVisibility = true,
        bool rollbackChangesVisibility = true,
        bool failAfterHide = false,
        bool captureSucceeds = true,
        bool guardArmSucceeds = true,
        bool lockAcquireSucceeds = true,
        bool originallyVisible = true,
        TaskbarLeaseDocument? existingDocument = null)
    {
        events ??= new List<string>();
        var platform = FakeTaskbarPlatform.PrimaryShellTrayWnd(
            handle: 42,
            processId: 100,
            processStartTicks: 1234,
            monitor: 7,
            visible: originallyVisible,
            showCommand: NativeMethods.SW_SHOW,
            events: events);
        if (!captureSucceeds)
            platform.SetClassName(42, "ReusedWindow");

        platform.HideChangesVisibility = hideChangesVisibility;
        platform.ShowChangesVisibility = rollbackChangesVisibility;

        var journal = new FakeTaskbarLeaseJournal(events, existingDocument);
        if (failAfterHide)
            journal.ThrowOnWriteStatus = TaskbarLeaseStatus.Active;

        var fileLock = new FakeTaskbarLeaseLock(events)
        {
            FailAcquire = !lockAcquireSucceeds,
        };
        var guard = new FakeTaskbarRecoveryGuard(events)
        {
            ArmSucceeds = guardArmSucceeds,
        };
        var service = new TaskbarWindowService(platform);
        var lease = new TaskbarLease(
            service,
            journal,
            fileLock,
            guard,
            10,
            20,
            TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero);

        return new LeaseHarness(lease, platform, journal, fileLock, guard, events);
    }
}

internal sealed class RecoveryHarness
{
    private RecoveryHarness(
        TaskbarRecoveryService service,
        FakeTaskbarPlatform platform,
        FakeTaskbarLeaseJournal journal,
        FakeTaskbarLeaseLock fileLock,
        FakeProcessInspector inspector,
        IList<string> events)
    {
        Service = service;
        Platform = platform;
        Journal = journal;
        Lock = fileLock;
        Inspector = inspector;
        Events = events;
    }

    public TaskbarRecoveryService Service { get; }

    public FakeTaskbarPlatform Platform { get; }

    public FakeTaskbarLeaseJournal Journal { get; }

    public FakeTaskbarLeaseLock Lock { get; }

    public FakeProcessInspector Inspector { get; }

    public IList<string> Events { get; }

    public static RecoveryHarness ActiveHiddenLease(
        string leaseId = "11111111-1111-1111-1111-111111111111")
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
        var journal = new FakeTaskbarLeaseJournal(
            events,
            LeaseSamples.Active(leaseId, handle: 42));
        var fileLock = new FakeTaskbarLeaseLock(events);
        var inspector = new FakeProcessInspector();
        var service = new TaskbarRecoveryService(
            new TaskbarWindowService(platform),
            journal,
            fileLock,
            inspector,
            TimeSpan.Zero);

        return new RecoveryHarness(service, platform, journal, fileLock, inspector, events);
    }
}

internal sealed class FakeWatchdogProcess : IWatchdogProcess
{
    private bool _hasExited;
    private int _hasExitedReadCalls;

    public int Id { get; init; } = 4321;

    public bool SignalsReady { get; set; } = true;

    public bool HasExited
    {
        get
        {
            var readCall = Interlocked.Increment(ref _hasExitedReadCalls);
            HasExitedThreadIds.Add(Environment.CurrentManagedThreadId);
            OnHasExitedRead?.Invoke(readCall);
            if (BlockOnSecondHasExitedRead && readCall == 2)
            {
                SecondHasExitedReadEntered.TrySetResult(true);
                AllowSecondHasExitedRead.Task.GetAwaiter().GetResult();
            }

            return _hasExited;
        }
        set => _hasExited = value;
    }

    public bool Terminated { get; private set; }

    public int TerminateCalls { get; private set; }

    public Exception? TerminateException { get; set; }

    public bool WaitForExitResult { get; set; } = true;

    public Exception? WaitForExitException { get; set; }

    public Queue<Exception?>? WaitForExitExceptions { get; set; }

    public bool BlockWaitForExit { get; set; }

    public TaskCompletionSource<bool> WaitForExitEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> AllowWaitForExit { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int WaitForExitCalls { get; private set; }

    public int? WaitForExitThreadId { get; private set; }

    public bool Disposed { get; private set; }

    public int DisposeCalls { get; private set; }

    public bool BlockOnSecondHasExitedRead { get; set; }

    public TaskCompletionSource<bool> SecondHasExitedReadEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> AllowSecondHasExitedRead { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool BlockDispose { get; set; }

    public TaskCompletionSource<bool> DisposeEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> AllowDispose { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Action<int>? OnHasExitedRead { get; set; }

    public List<int> HasExitedThreadIds { get; } = new();

    public void Terminate()
    {
        TerminateCalls++;
        if (TerminateException is not null)
            throw TerminateException;

        Terminated = true;
        _hasExited = true;
    }

    public bool WaitForExit(TimeSpan timeout)
    {
        WaitForExitCalls++;
        WaitForExitThreadId = Environment.CurrentManagedThreadId;
        if (BlockWaitForExit)
        {
            WaitForExitEntered.TrySetResult(true);
            AllowWaitForExit.Task.GetAwaiter().GetResult();
        }

        if (WaitForExitExceptions is { Count: > 0 })
        {
            var exception = WaitForExitExceptions.Dequeue();
            if (exception is not null)
                throw exception;
        }

        if (WaitForExitException is not null)
            throw WaitForExitException;

        if (WaitForExitResult)
            _hasExited = true;
        return WaitForExitResult;
    }

    public void Dispose()
    {
        DisposeCalls++;
        if (BlockDispose)
        {
            DisposeEntered.TrySetResult(true);
            AllowDispose.Task.GetAwaiter().GetResult();
        }

        Disposed = true;
    }
}

internal sealed class FakeWatchdogEvent : IWatchdogEvent
{
    private readonly EventWaitHandle _handle;

    public FakeWatchdogEvent(EventWaitHandle handle)
    {
        _handle = handle;
    }

    public Exception? SetException { get; set; }

    public bool Disposed { get; private set; }

    public int SetCalls { get; private set; }

    public bool WaitOne(TimeSpan timeout)
        => _handle.WaitOne(timeout);

    public void Set()
    {
        SetCalls++;
        if (SetException is not null)
            throw SetException;

        _handle.Set();
    }

    public void Dispose()
    {
        Disposed = true;
        _handle.Dispose();
    }
}

internal sealed class FakeWatchdogEventFactory
{
    public Exception? StopSetException { get; set; }

    public List<FakeWatchdogEvent> Created { get; } = new();

    public FakeWatchdogEvent? StopEvent { get; private set; }

    public IWatchdogEvent Create(string name)
    {
        var handle = new EventWaitHandle(
            initialState: false,
            EventResetMode.ManualReset,
            name,
            out var createdNew);
        if (!createdNew)
        {
            handle.Dispose();
            throw new IOException("The fake event name was already in use.");
        }

        var fake = new FakeWatchdogEvent(handle);
        if (name.EndsWith(".stop", StringComparison.Ordinal))
        {
            fake.SetException = StopSetException;
            StopEvent = fake;
        }

        Created.Add(fake);
        return fake;
    }
}

internal sealed class FakeWatchdogDelay
{
    private TaskCompletionSource<bool> _release =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> Entered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Calls { get; private set; }

    public TimeSpan LastDelay { get; private set; }

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Calls++;
        LastDelay = delay;
        Entered.TrySetResult(true);
        return WaitForReleaseAsync(cancellationToken);
    }

    public void Release()
        => _release.TrySetResult(true);

    private async Task WaitForReleaseAsync(CancellationToken cancellationToken)
    {
        var cancellation = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        var completed = await Task.WhenAny(_release.Task, cancellation)
            .ConfigureAwait(false);
        await completed.ConfigureAwait(false);
    }
}

internal sealed class FakeWatchdogProcessLauncher : IWatchdogProcessLauncher, IDisposable
{
    public FakeWatchdogProcessLauncher(FakeWatchdogProcess? process = null)
    {
        Process = process;
    }

    public FakeWatchdogProcess? Process { get; }

    public Func<FakeWatchdogProcess>? ProcessFactory { get; set; }

    public Exception? StartException { get; set; }

    public bool SetReadyEvent { get; set; } = true;

    public bool ExitAfterReady { get; set; }

    public bool BlockBeforeReady { get; set; }

    public Action? OnStart { get; set; }

    public int StartCalls { get; private set; }

    public string? LastPath { get; private set; }

    public IReadOnlyList<string>? LastArguments { get; private set; }

    public string? LastReadyEventName { get; private set; }

    public string? LastStopEventName { get; private set; }

    public List<string> ReadyEventNames { get; } = new();

    public List<string> StopEventNames { get; } = new();

    public EventWaitHandle? StopObserver { get; private set; }

    public EventWaitHandle? ReadyObserver { get; private set; }

    public List<EventWaitHandle> StopObservers { get; } = new();

    public List<EventWaitHandle> ReadyObservers { get; } = new();

    public TaskCompletionSource<bool> StartEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> ReadyEntered { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> AllowReady { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IWatchdogProcess Start(
        string executablePath,
        IReadOnlyList<string> arguments)
    {
        StartCalls++;
        LastPath = executablePath;
        LastArguments = arguments.ToArray();
        StartEntered.TrySetResult(true);

        if (StartException is not null)
            throw StartException;

        var process = ProcessFactory?.Invoke() ?? Process
            ?? throw new InvalidOperationException("A fake process is required.");
        LastReadyEventName = ValueFor(arguments, "--ready-event");
        LastStopEventName = ValueFor(arguments, "--stop-event");
        ReadyEventNames.Add(LastReadyEventName);
        StopEventNames.Add(LastStopEventName);
        ReadyObserver = EventWaitHandle.OpenExisting(LastReadyEventName);
        ReadyObservers.Add(ReadyObserver);
        StopObserver = EventWaitHandle.OpenExisting(LastStopEventName);
        StopObservers.Add(StopObserver);
        OnStart?.Invoke();

        if (SetReadyEvent && process.SignalsReady)
        {
            if (BlockBeforeReady)
            {
                ReadyEntered.TrySetResult(true);
                AllowReady.Task.GetAwaiter().GetResult();
            }

            using var readyEvent = EventWaitHandle.OpenExisting(LastReadyEventName);
            readyEvent.Set();
            if (ExitAfterReady)
                process.HasExited = true;
        }

        return process;
    }

    public void DisposeObservers()
    {
        foreach (var observer in StopObservers)
            observer.Dispose();
        foreach (var observer in ReadyObservers)
            observer.Dispose();
        StopObservers.Clear();
        ReadyObservers.Clear();
        StopObserver = null;
        ReadyObserver = null;
    }

    public void Dispose()
        => DisposeObservers();

    private static string ValueFor(IReadOnlyList<string> arguments, string key)
    {
        for (var index = 0; index < arguments.Count - 1; index += 2)
        {
            if (string.Equals(arguments[index], key, StringComparison.Ordinal))
                return arguments[index + 1];
        }

        throw new InvalidOperationException($"Missing fake argument {key}.");
    }
}

internal sealed class FakeWatchdogRuntime : ITaskbarWatchdogRuntime
{
    private readonly TaskbarWatchdogSignal _signal;

    public FakeWatchdogRuntime(
        TaskbarWatchdogSignal signal,
        IList<string> events)
    {
        _signal = signal;
        Events = events;
    }

    public IList<string> Events { get; }

    public Exception? WaitException { get; set; }

    public int ReadyCalls { get; private set; }

    public int WaitCalls { get; private set; }

    public bool WaitObservedAfterReady { get; private set; }

    public static FakeWatchdogRuntime OwnerExitsAfterReady(IList<string> events)
        => new(TaskbarWatchdogSignal.OwnerExited, events);

    public static FakeWatchdogRuntime StopAfterReady(IList<string> events)
        => new(TaskbarWatchdogSignal.Stop, events);

    public static FakeWatchdogRuntime StopAndOwnerExit(IList<string> events)
        => new(TaskbarWatchdogSignal.Stop, events);

    public void SignalReady()
    {
        ReadyCalls++;
        Events.Add("ready");
    }

    public Task<TaskbarWatchdogSignal> WaitForStopOrOwnerExitAsync(
        CancellationToken cancellationToken)
    {
        WaitCalls++;
        WaitObservedAfterReady = ReadyCalls == 1;
        if (WaitException is not null)
            return Task.FromException<TaskbarWatchdogSignal>(WaitException);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<TaskbarWatchdogSignal>(cancellationToken);
        return Task.FromResult(_signal);
    }
}

internal sealed class FakeTaskbarRecoveryService : ITaskbarRecoveryService
{
    public FakeTaskbarRecoveryService(IList<string> events, bool succeeds)
    {
        Events = events;
        Succeeds = succeeds;
    }

    public IList<string> Events { get; }

    public bool Succeeds { get; set; }

    public Exception? RecoverException { get; set; }

    public string? RequiredLeaseId { get; set; }

    public int Calls { get; private set; }

    public string? LastLeaseId { get; private set; }

    public bool StaleRecoverySucceeds { get; set; }

    public Exception? StaleRecoveryException { get; set; }

    public string? StaleRecoveryError { get; set; }

    public TaskbarRecoveryResult? StaleRecoveryResult { get; set; }

    public int StaleRecoveryCalls { get; private set; }

    public Task<TaskbarRecoveryResult> TryRecoverAsync(
        string expectedLeaseId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        LastLeaseId = expectedLeaseId;
        Events.Add($"recover:{expectedLeaseId}");
        if (RecoverException is not null)
            return Task.FromException<TaskbarRecoveryResult>(RecoverException);
        var succeeded = Succeeds
            && (RequiredLeaseId is null
                || string.Equals(RequiredLeaseId, expectedLeaseId, StringComparison.Ordinal));
        return Task.FromResult(
            new TaskbarRecoveryResult(
                succeeded,
                0,
                Array.Empty<long>(),
                succeeded ? null : "fake recovery failure"));
    }

    public Task<TaskbarRecoveryResult> TryRecoverStaleAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StaleRecoveryCalls++;
        Events.Add("recover-stale");

        if (StaleRecoveryException is not null)
            return Task.FromException<TaskbarRecoveryResult>(StaleRecoveryException);

        if (StaleRecoveryResult is not null)
            return Task.FromResult(StaleRecoveryResult);

        return Task.FromResult(
            new TaskbarRecoveryResult(
                Succeeded: StaleRecoverySucceeds,
                RestoredCount: 0,
                FailedHandles: Array.Empty<long>(),
                Error: StaleRecoverySucceeds
                    ? null
                    : StaleRecoveryError ?? "fake stale recovery failure"));
    }
}

internal sealed class FakeTaskbarLease : ITaskbarLease
{
    public FakeTaskbarLease(IList<string>? events = null)
    {
        Events = events ?? new List<string>();
    }

    public IList<string> Events { get; }

    public TaskbarLeaseState State { get; private set; } = TaskbarLeaseState.Released;

    public bool AcquireResult { get; set; } = true;

    public bool ReconcileResult { get; set; } = true;

    public bool ReleaseResult { get; set; } = true;

    public Exception? AcquireException { get; set; }

    public Exception? ReconcileException { get; set; }

    public Exception? ReleaseException { get; set; }

    public Func<CancellationToken, Task<bool>>? AcquireHandler { get; set; }

    public Func<CancellationToken, Task<bool>>? ReconcileHandler { get; set; }

    public Func<CancellationToken, Task<bool>>? ReleaseHandler { get; set; }

    public int AcquireCalls { get; private set; }

    public int ReconcileCalls { get; private set; }

    public int ReconcileAttempts { get; private set; }

    public int ReleaseCalls { get; private set; }

    public int DisposeCalls { get; private set; }

    public bool IsDisposed { get; private set; }

    public bool CoordinationBlocked { get; private set; }

    public Action? DisposeCallback { get; set; }

    public Task? DisposeBarrier { get; set; }

    public async Task<bool> AcquireAsync(CancellationToken cancellationToken = default)
    {
        if (CoordinationBlocked)
            return false;

        AcquireCalls++;
        Events.Add("acquire");
        cancellationToken.ThrowIfCancellationRequested();
        if (AcquireException is not null)
            throw AcquireException;

        var succeeded = AcquireHandler is null
            ? AcquireResult
            : await AcquireHandler(cancellationToken).ConfigureAwait(false);
        if (succeeded)
            State = TaskbarLeaseState.Active;
        return succeeded;
    }

    public async Task<bool> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        ReconcileAttempts++;
        if (CoordinationBlocked)
            return false;

        ReconcileCalls++;
        Events.Add("reconcile");
        cancellationToken.ThrowIfCancellationRequested();
        if (ReconcileException is not null)
            throw ReconcileException;

        return ReconcileHandler is null
            ? ReconcileResult
            : await ReconcileHandler(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ReleaseAsync(CancellationToken cancellationToken = default)
    {
        if (IsDisposed)
            return false;

        ReleaseCalls++;
        Events.Add("release");
        cancellationToken.ThrowIfCancellationRequested();
        if (ReleaseException is not null)
            throw ReleaseException;

        var succeeded = ReleaseHandler is null
            ? ReleaseResult
            : await ReleaseHandler(cancellationToken).ConfigureAwait(false);
        State = succeeded
            ? TaskbarLeaseState.Released
            : TaskbarLeaseState.RecoveryPending;
        return succeeded;
    }

    public async ValueTask DisposeAsync()
    {
        DisposeCalls++;
        Events.Add("lease-dispose");
        if (IsDisposed)
            return;

        IsDisposed = true;
        CoordinationBlocked = true;
        if (State == TaskbarLeaseState.Active)
        {
            State = ReleaseResult
                ? TaskbarLeaseState.Released
                : TaskbarLeaseState.RecoveryPending;
        }

        DisposeCallback?.Invoke();
        if (DisposeBarrier is not null)
            await DisposeBarrier.ConfigureAwait(false);
    }
}

internal sealed class FakeAppSettingsStore : IAppSettingsStore
{
    public FakeAppSettingsStore(
        IList<string> events,
        AppSettings settings,
        string? filePath = null)
    {
        Events = events;
        Current = Copy(settings);
        FilePath = filePath;
    }

    public IList<string> Events { get; }

    public AppSettings Current { get; private set; }

    public string? FilePath { get; }

    public Exception? LoadException { get; set; }

    public Exception? SaveException { get; set; }

    public Action? BeforeLoad { get; set; }

    public int LoadCalls { get; private set; }

    public int SaveCalls { get; private set; }

    public byte[] OriginalBytes { get; private set; } = Array.Empty<byte>();

    public AppSettings Load()
    {
        LoadCalls++;
        BeforeLoad?.Invoke();
        Events.Add("settings-load");
        if (LoadException is not null)
            throw LoadException;

        return Copy(Current);
    }

    public void Save(AppSettings settings)
    {
        SaveCalls++;
        Events.Add(settings.HideWindowsTaskbar ? "save-true" : "save-false");
        if (SaveException is not null)
            throw SaveException;

        Current = Copy(settings);
    }

    public void SetSourceBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (string.IsNullOrWhiteSpace(FilePath))
            throw new InvalidOperationException("A test-owned source path is required.");

        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllBytes(FilePath, bytes);
        OriginalBytes = bytes.ToArray();
    }

    public byte[] ReadSourceBytes()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
            throw new InvalidOperationException("A test-owned source path is required.");

        return File.ReadAllBytes(FilePath);
    }

    private static AppSettings Copy(AppSettings settings)
        => new()
        {
            SchemaVersion = settings.SchemaVersion,
            HideWindowsTaskbar = settings.HideWindowsTaskbar,
        };
}

internal sealed class TestOwnedDirectory : IDisposable
{
    private int _disposed;

    public TestOwnedDirectory()
    {
        DirectoryPath = Path.Combine(
            Path.GetTempPath(),
            "MacDock.Task6.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
    }

    public string DirectoryPath { get; }

    public string FilePath(string fileName)
        => Path.Combine(DirectoryPath, fileName);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (Directory.Exists(DirectoryPath))
            Directory.Delete(DirectoryPath, recursive: true);
    }
}

internal sealed class CoordinatorHarness
{
    private CoordinatorHarness(
        TaskbarCoordinator coordinator,
        FakeTaskbarLease lease,
        FakeAppSettingsStore settings,
        IList<string> events)
    {
        Coordinator = coordinator;
        Lease = lease;
        Settings = settings;
        Events = events;
    }

    public TaskbarCoordinator Coordinator { get; }

    public FakeTaskbarLease Lease { get; }

    public FakeAppSettingsStore Settings { get; }

    public IList<string> Events { get; }

    public static CoordinatorHarness Create(
        IList<string>? events = null,
        bool acquireResult = true,
        bool reconcileResult = true,
        bool releaseResult = true,
        bool changesAllowed = true,
        string? unavailableReason = null,
        bool persistedTaskbarSetting = false)
    {
        events ??= new List<string>();
        var initialSettings = new AppSettings
        {
            HideWindowsTaskbar = persistedTaskbarSetting,
        };
        var settings = new FakeAppSettingsStore(events, initialSettings);
        var lease = new FakeTaskbarLease(events)
        {
            AcquireResult = acquireResult,
            ReconcileResult = reconcileResult,
            ReleaseResult = releaseResult,
        };
        var coordinator = new TaskbarCoordinator(
            lease,
            settings,
            initialSettings,
            changesAllowed,
            unavailableReason);

        return new CoordinatorHarness(coordinator, lease, settings, events);
    }
}

internal sealed class StartupGateHarness : IDisposable
{
    private StartupGateHarness(
        TestOwnedDirectory directory,
        TaskbarStartupGate gate,
        FakeTaskbarRecoveryService recovery,
        FakeAppSettingsStore settings)
    {
        Directory = directory;
        Gate = gate;
        Recovery = recovery;
        Settings = settings;
    }

    public TestOwnedDirectory Directory { get; }

    public TaskbarStartupGate Gate { get; }

    public FakeTaskbarRecoveryService Recovery { get; }

    public FakeAppSettingsStore Settings { get; }

    public static StartupGateHarness Create(
        IList<string>? events = null,
        bool recoverySucceeds = false,
        bool persistedTaskbarSetting = false)
    {
        events ??= new List<string>();
        var directory = new TestOwnedDirectory();
        var settings = new FakeAppSettingsStore(
            events,
            new AppSettings { HideWindowsTaskbar = persistedTaskbarSetting },
            directory.FilePath("settings.json"));
        settings.SetSourceBytes(
            persistedTaskbarSetting
                ? [0x7B, 0x22, 0x48, 0x69, 0x64, 0x65, 0x22, 0x3A, 0x74, 0x72, 0x75, 0x65, 0x7D]
                : [0x7B, 0x22, 0x48, 0x69, 0x64, 0x65, 0x22, 0x3A, 0x66, 0x61, 0x6C, 0x73, 0x65, 0x7D]);
        var recovery = new FakeTaskbarRecoveryService(events, succeeds: true)
        {
            StaleRecoverySucceeds = recoverySucceeds,
        };
        var gate = new TaskbarStartupGate(recovery, settings);
        return new StartupGateHarness(directory, gate, recovery, settings);
    }

    public void Dispose()
        => Directory.Dispose();
}

internal sealed class RealStartupRecoveryHarness : IDisposable
{
    private RealStartupRecoveryHarness(
        TestOwnedDirectory directory,
        FakeTaskbarPlatform platform,
        FakeProcessInspector inspector,
        TaskbarLeaseJournal journal,
        TaskbarRecoveryService recovery,
        FakeAppSettingsStore settings,
        TaskbarStartupGate gate,
        IList<string> events)
    {
        Directory = directory;
        Platform = platform;
        Inspector = inspector;
        Journal = journal;
        Recovery = recovery;
        Settings = settings;
        Gate = gate;
        Events = events;
    }

    public TestOwnedDirectory Directory { get; }

    public FakeTaskbarPlatform Platform { get; }

    public FakeProcessInspector Inspector { get; }

    public TaskbarLeaseJournal Journal { get; }

    public TaskbarRecoveryService Recovery { get; }

    public FakeAppSettingsStore Settings { get; }

    public TaskbarStartupGate Gate { get; }

    public IList<string> Events { get; }

    public string JournalPath => Journal.FilePath;

    public string LockPath { get; private set; } = string.Empty;

    public const string LeaseId = "11111111-1111-1111-1111-111111111111";

    public static RealStartupRecoveryHarness Create(
        bool persistedTaskbarSetting = true)
    {
        var directory = new TestOwnedDirectory();
        var events = new List<string>();
        var platform = FakeTaskbarPlatform.PrimaryShellTrayWnd(
            handle: 42,
            processId: 10,
            processStartTicks: 20,
            monitor: 30,
            visible: false,
            showCommand: NativeMethods.SW_SHOW,
            events: events);
        var inspector = new FakeProcessInspector();
        var journalPath = directory.FilePath("taskbar-lease.json");
        var lockPath = directory.FilePath("taskbar-lease.lock");
        var journal = new TaskbarLeaseJournal(journalPath);
        var recovery = new TaskbarRecoveryService(
            new TaskbarWindowService(platform),
            journal,
            new TaskbarLeaseFileLock(lockPath),
            inspector,
            TimeSpan.Zero);
        var settings = new FakeAppSettingsStore(
            events,
            new AppSettings { HideWindowsTaskbar = persistedTaskbarSetting },
            directory.FilePath("settings.json"));
        settings.SetSourceBytes([0x11, 0x22, 0x33, 0x44]);
        var gate = new TaskbarStartupGate(recovery, settings);

        return new RealStartupRecoveryHarness(
            directory,
            platform,
            inspector,
            journal,
            recovery,
            settings,
            gate,
            events)
        {
            LockPath = lockPath,
        };
    }

    public void WriteValidResidualJournal()
        => Journal.Write(LeaseSamples.Active(LeaseId, handle: 42));

    public void WriteJournalBytes(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        File.WriteAllBytes(JournalPath, bytes);
    }

    public byte[] ReadJournalBytes()
        => File.ReadAllBytes(JournalPath);

    public void Dispose()
        => Directory.Dispose();
}
