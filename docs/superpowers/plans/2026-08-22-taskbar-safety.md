# MacDock Safe Taskbar Takeover Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the unsafe taskbar-hiding attempt with an opt-in, primary-monitor-only lease that never writes taskbar registry state and can restore the taskbar through a watchdog after the UI process exits unexpectedly.

**Architecture:** Core owns a testable Win32 boundary, an atomic lease journal, a serialized `TaskbarLease`, and shared recovery logic. A small non-elevated watchdog waits for the UI process and invokes recovery after abnormal exit. `App` owns the lifecycle; `MainViewModel` has no taskbar side effects, and `DockWindow` only forwards Shell messages.

**Tech Stack:** C# 12, .NET 8, WPF, xUnit, CommunityToolkit.Mvvm, Win32 P/Invoke, `System.Text.Json`, NLog.

**Spec:** `docs/superpowers/specs/2026-08-22-taskbar-safety-design.md`

## Global Constraints

- Work directly in the current `main` working tree; do not create a branch or worktree.
- Preserve existing user changes; never run `git reset`, `git checkout --`, or broad `git clean`.
- Child agents use `gpt-5.6-luna`, `max` reasoning, and standard service; omit `service_tier`.
- Do not launch MacDock or execute a test that touches the real Explorer taskbar on this machine.
- The user's approval of Design A authorizes replacing the untracked unsafe `TaskbarHider.cs` and its taskbar-only hunks in mixed files; it does not authorize removing `WindowMonitor`, icon, running-state, fish-eye, or other unrelated user work.
- `HideWindowsTaskbar` defaults to `false`.
- Only primary-monitor `Shell_TrayWnd` is eligible. Never touch `Shell_SecondaryTrayWnd`, `ApplicationFrameHost`, or arbitrary Explorer windows.
- Remove taskbar registry writes, `ABM_SETSTATE`, and the three-second re-hide loop.
- Every system-facing test uses fakes. Real Shell tests require separate approval and a disposable Windows VM.
- Do not commit mixed pre-existing working-tree changes. Each task ends with a review checkpoint; commits wait until overlapping M3 files are fully repaired.

---

## File Map

- `MacDock.Core/Services/Taskbar/TaskbarWindowSnapshot.cs`: serializable HWND identity and original state.
- `MacDock.Core/Services/Taskbar/ITaskbarPlatform.cs`: injectable OS boundary.
- `MacDock.Core/Services/Taskbar/Win32TaskbarPlatform.cs`: production P/Invoke adapter.
- `MacDock.Core/Services/Taskbar/TaskbarWindowService.cs`: exact scope, identity checks, hide/restore.
- `MacDock.Core/Services/AtomicJsonFile.cs`: flushed temp-file plus atomic replacement.
- `MacDock.Core/Services/Taskbar/TaskbarLeaseFileLock.cs`: cross-process exclusive lock shared by UI and watchdog.
- `MacDock.Core/Services/Taskbar/TaskbarLeaseDocument.cs` and `TaskbarLeaseJournal.cs`: durable recovery journal.
- `MacDock.Core/Services/Taskbar/TaskbarLease.cs`: serialized state machine.
- `MacDock.Core/Services/Taskbar/TaskbarRecoveryService.cs`: shared idempotent recovery.
- `MacDock.Core/Services/Taskbar/TaskbarWatchdogClient.cs`: ready/stop handshake.
- `MacDock.Watchdog/*`: non-elevated helper.
- `MacDock.Core/Models/AppSettings.cs` and `AppSettingsStore.cs`: opt-in setting.
- `MacDock.Core/Services/Taskbar/TaskbarCoordinator.cs`: application-facing, UI-independent facade.
- `MacDock.Tests/TaskbarTestDoubles.cs`: shared in-memory platform, journal, guard, launcher, lease, and settings fakes.
- `App.xaml.cs` owns lifecycle; `DockWindow.xaml.cs` forwards messages; `MainViewModel.cs` loses taskbar ownership.

---

### Task 1: Exact Primary-Taskbar Boundary

**Files:**
- Create: `MacDock.Core/Services/Taskbar/TaskbarWindowSnapshot.cs`
- Create: `MacDock.Core/Services/Taskbar/ITaskbarPlatform.cs`
- Create: `MacDock.Core/Services/Taskbar/Win32TaskbarPlatform.cs`
- Create: `MacDock.Core/Services/Taskbar/TaskbarWindowService.cs`
- Create: `MacDock.Core/Properties/AssemblyInfo.cs`
- Modify: `MacDock.Core/Interop/NativeMethods.cs`
- Create: `MacDock.Tests/TaskbarTestDoubles.cs`
- Test: `MacDock.Tests/TaskbarWindowServiceTests.cs`
- Test: `MacDock.Tests/NativeAbiTests.cs`

**Interfaces:**
- Produces `TaskbarWindowSnapshot`, `ITaskbarPlatform`, and `TaskbarWindowService` for Tasks 3 and 4.
- Production code must not enumerate windows by process name.
- `AssemblyInfo.cs` grants `InternalsVisibleTo("MacDock.Tests")` so ABI tests inspect internal interop types without making them public.

- [ ] **Step 1: Write failing scope and identity tests**

```csharp
[Fact]
public void CapturePrimary_ReturnsOnlyVerifiedPrimaryShellTrayWindow()
{
    var platform = FakeTaskbarPlatform.PrimaryShellTrayWnd(
        handle: 42, processId: 100, processStartTicks: 1234,
        monitor: 7, visible: true, showCommand: NativeMethods.SW_SHOW);

    var snapshot = new TaskbarWindowService(platform).CapturePrimary();

    Assert.NotNull(snapshot);
    Assert.Equal(42, snapshot.Handle);
    Assert.Equal("Shell_TrayWnd", snapshot.ClassName);
    Assert.True(snapshot.WasVisible);
}

[Theory]
[InlineData("ApplicationFrameWindow", "ApplicationFrameHost", 7)]
[InlineData("CabinetWClass", "explorer", 7)]
[InlineData("Shell_SecondaryTrayWnd", "explorer", 7)]
[InlineData("Shell_TrayWnd", "explorer", 8)]
public void CapturePrimary_RejectsAnythingOutsideExactScope(
    string className, string processName, long monitor)
{
    var platform = FakeTaskbarPlatform.Window(42, className, processName, monitor);
    Assert.Null(new TaskbarWindowService(platform).CapturePrimary());
}

[Fact]
public void Restore_WhenSameWindowMovedMonitors_RestoresLeaseChange()
{
    var harness = TaskbarWindowHarness.HiddenPrimaryThenMoved(handle: 42);

    Assert.True(harness.Service.TryRestore(harness.Snapshot));

    Assert.True(harness.Platform.IsWindowVisible((nint)42));
}
```

`TaskbarTestDoubles.cs` defines `FakeTaskbarPlatform : ITaskbarPlatform` backed by a `Dictionary<long, FakeWindowState>`. Its `PrimaryShellTrayWnd(...)` and `Window(...)` factories populate class, process, process-start, monitor, visibility, and show-command values; every mutation appends a string to an injected `IList<string>` so later ordering tests can assert exact calls. `TaskbarWindowHarness.HiddenPrimaryThenMoved(...)` captures a visible primary snapshot, applies the fake hide, then changes only the fake's monitor before restore.

- [ ] **Step 2: Run red**

```powershell
dotnet test MacDock.Tests --filter "FullyQualifiedName~TaskbarWindowServiceTests|FullyQualifiedName~NativeAbiTests" --no-restore
```

Expected: compile failure because Taskbar types do not exist.

- [ ] **Step 3: Implement exact interfaces**

```csharp
public enum TaskbarWindowMutationState { Unchanged, HidePending, HiddenByLease }

public sealed record TaskbarWindowSnapshot(
    long Handle,
    uint ProcessId,
    long ProcessStartTimeUtcTicks,
    string ClassName,
    long MonitorHandle,
    bool WasVisible,
    int ShowCommand,
    TaskbarWindowMutationState MutationState);

public interface ITaskbarPlatform
{
    nint FindWindow(string className);
    bool IsWindow(nint handle);
    string? GetWindowClassName(nint handle);
    uint GetWindowProcessId(nint handle);
    string? GetProcessName(uint processId);
    long? GetProcessStartTimeUtcTicks(uint processId);
    nint GetWindowMonitor(nint handle);
    nint GetPrimaryMonitor();
    bool IsWindowVisible(nint handle);
    int? GetWindowShowCommand(nint handle);
    bool SetWindowShowState(nint handle, int command);
}
```

`TaskbarWindowService.CapturePrimary()` requires exact class `Shell_TrayWnd`, process `explorer`, non-zero PID/start ticks, primary monitor equality, and valid visibility/placement. Keep scope and identity separate: hide eligibility checks the current primary monitor, while restore identity checks HWND validity, exact class, PID, and process start time. A monitor handle is transient across `WM_DISPLAYCHANGE`; restore must still undo a change made to the same verified window after it moves monitors.

`Win32TaskbarPlatform.SetWindowShowState` returns Win32 `ShowWindow`'s actual semantic: whether the window was visible *before* the call, never whether the operation succeeded. `TaskbarWindowService` determines success only by re-reading `IsWindowVisible`. During hide, a false return means another actor won the pre-call race and the lease must not claim that transition; during restore the return is informational only. The fake's `hideChangesVisibility`/`showChangesVisibility` switches control the postcondition independently of the prior-visibility return.

- [ ] **Step 4: Correct and minimize P/Invoke**

Add `IsWindow`, `GetWindowPlacement`, `MonitorFromWindow`, and `MonitorFromPoint`. Initialize `WINDOWPLACEMENT.length` to `Marshal.SizeOf<WINDOWPLACEMENT>()` and use `[return: MarshalAs(UnmanagedType.Bool)]` for BOOL. Remove the added `APPBARDATA`, `SHAppBarMessage`, taskbar registry declarations, `WM_SETTINGCHANGE`, and `SendMessageTimeout`; retain shared WinEvent/window declarations used by `WindowMonitor`.

```csharp
[StructLayout(LayoutKind.Sequential)]
internal struct WINDOWPLACEMENT
{
    public uint length;
    public uint flags;
    public int showCmd;
    public POINT ptMinPosition;
    public POINT ptMaxPosition;
    public RECT rcNormalPosition;
}
```

- [ ] **Step 5: Add ABI tests and run green**

```csharp
[Fact]
public void WindowPlacement_HasExpectedLayout()
{
    Assert.Equal(44, Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>());
    Assert.Equal(8, Marshal.OffsetOf<NativeMethods.WINDOWPLACEMENT>("showCmd").ToInt32());
}
```

Run the Task 1 command again. Expected: all filtered tests pass without a real taskbar call.

- [ ] **Step 6: Review checkpoint**

```powershell
git diff --check
git diff -- MacDock.Core/Interop/NativeMethods.cs MacDock.Core/Services/Taskbar MacDock.Tests/TaskbarWindowServiceTests.cs MacDock.Tests/NativeAbiTests.cs
```

Verify production taskbar code contains no `ApplicationFrameHost`, `StuckRects3`, `ABM_GETSTATE`, `ABM_SETSTATE`, `SHAppBarMessage`, taskbar registry P/Invokes, or `Shell_SecondaryTrayWnd`. Do not commit.

---

### Task 2: Atomic Journal Primitive

**Files:**
- Create: `MacDock.Core/Services/AtomicJsonFile.cs`
- Create: `MacDock.Core/Services/Taskbar/TaskbarLeaseDocument.cs`
- Create: `MacDock.Core/Services/Taskbar/ITaskbarLeaseJournal.cs`
- Create: `MacDock.Core/Services/Taskbar/TaskbarLeaseJournal.cs`
- Create: `MacDock.Core/Services/Taskbar/ITaskbarLeaseLock.cs`
- Create: `MacDock.Core/Services/Taskbar/TaskbarLeaseFileLock.cs`
- Modify: `MacDock.Core/AppPaths.cs`
- Test: `MacDock.Tests/AtomicJsonFileTests.cs`
- Test: `MacDock.Tests/TaskbarLeaseJournalTests.cs`
- Test: `MacDock.Tests/TaskbarLeaseFileLockTests.cs`

**Interfaces:**
- Consumes `TaskbarWindowSnapshot`.
- Produces `AtomicJsonFile<T>`, `ITaskbarLeaseJournal`, and `ITaskbarLeaseLock` for Tasks 3, 4, and 6.

- [ ] **Step 1: Write failing round-trip and corruption tests**

```csharp
[Fact]
public void WriteThenRead_RoundTripsCompleteLease()
{
    var path = Path.Combine(_tempDirectory, "taskbar-lease.json");
    var journal = new TaskbarLeaseJournal(path);
    var document = LeaseSamples.Active(
        "11111111-1111-1111-1111-111111111111", handle: 42);

    journal.Write(document);

    var loaded = Assert.IsType<TaskbarLeaseDocument>(journal.Read());
    Assert.Equal(document.LeaseId, loaded.LeaseId);
    Assert.Equal(document.Status, loaded.Status);
    Assert.Equal(document.Windows.ToArray(), loaded.Windows.ToArray());
    Assert.Empty(Directory.GetFiles(_tempDirectory, "*.tmp"));
}

[Fact]
public void Read_CorruptJsonThrowsAndPreservesOriginal()
{
    var path = Path.Combine(_tempDirectory, "taskbar-lease.json");
    File.WriteAllText(path, "{broken");
    var journal = new TaskbarLeaseJournal(path);

    Assert.Throws<InvalidDataException>(() => journal.Read());
    Assert.Equal("{broken", File.ReadAllText(path));
}

[Theory]
[InlineData("{\"SchemaVersion\":999}")]
[InlineData("{\"SchemaVersion\":1,\"LeaseId\":\"not-a-guid\"}")]
public void Read_InvalidSchemaThrowsAndPreservesOriginal(string json)
{
    var path = Path.Combine(_tempDirectory, "taskbar-lease.json");
    File.WriteAllText(path, json);

    Assert.Throws<InvalidDataException>(() => new TaskbarLeaseJournal(path).Read());
    Assert.Equal(json, File.ReadAllText(path));
}
```

- [ ] **Step 2: Run red**

```powershell
dotnet test MacDock.Tests --filter "FullyQualifiedName~AtomicJsonFileTests|FullyQualifiedName~TaskbarLeaseJournalTests|FullyQualifiedName~TaskbarLeaseFileLockTests" --no-restore
```

Expected: compile failure because journal types are missing.

- [ ] **Step 3: Define schema and contract**

```csharp
public enum TaskbarLeaseStatus { Prepared, Active, Releasing }

public sealed record TaskbarLeaseDocument(
    int SchemaVersion,
    string LeaseId,
    int OwnerProcessId,
    long OwnerProcessStartTimeUtcTicks,
    int? WatchdogProcessId,
    TaskbarLeaseStatus Status,
    long Generation,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<TaskbarWindowSnapshot> Windows)
{
    public const int CurrentSchemaVersion = 1;
}

public interface ITaskbarLeaseJournal
{
    string FilePath { get; }
    TaskbarLeaseDocument? Read();
    void Write(TaskbarLeaseDocument document);
    void Delete();
}

public interface ITaskbarLeaseLock
{
    Task<IAsyncDisposable?> TryAcquireAsync(
        TimeSpan timeout, CancellationToken cancellationToken = default);
}
```

Add `LeaseSamples.Active(string leaseId, long handle)` to `TaskbarTestDoubles.cs`; it returns schema version 1, owner PID 10/start ticks 20, Active status, generation 1, and one visible `Shell_TrayWnd` snapshot marked `HiddenByLease`.

- [ ] **Step 4: Implement atomic writes**

`AtomicJsonFile<T>.Write` creates the parent directory, writes UTF-8 JSON to `<path>.<guid>.tmp`, calls `Flush(flushToDisk: true)`, then uses same-volume `File.Replace` when the destination exists or `File.Move` when absent. A `finally` deletes only that generated temp path. `Read` wraps `JsonException` in `InvalidDataException` and preserves the source. Validate the complete lease schema on read: exact schema version, non-empty GUID lease ID, positive owner identity/generation, known status/mutation enum values, and internally consistent snapshots; validation failure is `InvalidDataException` and never rewrites the source.

`TaskbarLeaseFileLock` repeatedly attempts to open the exact lock path with `FileShare.None` until timeout/cancellation. Its returned handle owns that stream and releases it on async disposal; there is no thread-affine named `Mutex`. The OS releases the file handle on process death, allowing watchdog or the next UI process to recover. Add tests proving a second holder cannot enter, succeeds after disposal, and cancellation leaves no owned handle.

Add:

```csharp
public static string SettingsFile => Path.Combine(AppDataDirectory, "settings.json");
public static string TaskbarLeaseFile => Path.Combine(AppDataDirectory, "taskbar-lease.json");
public static string TaskbarLeaseLockFile => Path.Combine(AppDataDirectory, "taskbar-lease.lock");
```

- [ ] **Step 5: Run green and review**

Run the Task 2 command, including `TaskbarLeaseFileLockTests`. Confirm tests use unique temp directories and leave no `.tmp` files. Run `git diff --check`. Do not commit.

---

### Task 3: Serialized Lease and Shared Recovery

**Files:**
- Create: `MacDock.Core/Services/Taskbar/ITaskbarRecoveryGuard.cs`
- Create: `MacDock.Core/Services/Taskbar/IProcessInspector.cs`
- Create: `MacDock.Core/Services/Taskbar/ProcessInspector.cs`
- Create: `MacDock.Core/Services/Taskbar/TaskbarLease.cs`
- Create: `MacDock.Core/Services/Taskbar/ITaskbarRecoveryService.cs`
- Create: `MacDock.Core/Services/Taskbar/TaskbarRecoveryService.cs`
- Modify: `MacDock.Tests/TaskbarTestDoubles.cs`
- Test: `MacDock.Tests/TaskbarLeaseTests.cs`
- Test: `MacDock.Tests/TaskbarRecoveryServiceTests.cs`

**Interfaces:**
- Consumes `TaskbarWindowService`, `ITaskbarLeaseJournal`, and `ITaskbarLeaseLock`.
- Produces `ITaskbarLease`, `AcquireAsync()`, `ReconcileAsync()`, `ReleaseAsync()`, `TryRecoverAsync(expectedLeaseId)`, and `TryRecoverStaleAsync()`.

- [ ] **Step 1: Write ordering, rollback, Explorer-restart, race, and idempotency tests**

```csharp
[Fact]
public async Task Acquire_ArmsThenJournalsBeforeHiding()
{
    var events = new List<string>();
    var lease = LeaseHarness.Create(events, hideChangesVisibility: true).Lease;

    Assert.True(await lease.AcquireAsync());

    Assert.Equal(
        new[] { "lease-lock", "capture", "guard-arm", "journal-prepared", "journal-hide-pending", "hide", "journal-active" },
        events);
}

[Fact]
public async Task Acquire_WhenHideFails_RollsBackAndDisarms()
{
    var harness = LeaseHarness.Create(hideChangesVisibility: false);
    Assert.False(await harness.Lease.AcquireAsync());
    Assert.False(harness.Journal.Exists);
    Assert.True(harness.Guard.WasDisarmed);
    Assert.Equal(TaskbarLeaseState.Released, harness.Lease.State);
}

[Fact]
public async Task Acquire_WhenRollbackCannotBeVerified_KeepsGuardArmed()
{
    var harness = LeaseHarness.Create(
        hideChangesVisibility: true,
        rollbackChangesVisibility: false,
        failAfterHide: true);

    Assert.False(await harness.Lease.AcquireAsync());
    Assert.Equal(TaskbarLeaseState.RecoveryPending, harness.Lease.State);
    Assert.True(harness.Journal.Exists);
    Assert.False(harness.Guard.WasDisarmed);
}

[Fact]
public async Task Recovery_CalledTwice_DoesNotShowTwice()
{
    var harness = RecoveryHarness.ActiveHiddenLease();
    Assert.True((await harness.Service.TryRecoverAsync(
        "11111111-1111-1111-1111-111111111111")).Succeeded);
    Assert.True((await harness.Service.TryRecoverAsync(
        "11111111-1111-1111-1111-111111111111")).Succeeded);
    Assert.Equal(1, harness.Platform.ShowCalls);
}
```

- [ ] **Step 2: Run red**

```powershell
dotnet test MacDock.Tests --filter "FullyQualifiedName~TaskbarLeaseTests|FullyQualifiedName~TaskbarRecoveryServiceTests" --no-restore
```

Expected: compile failure because lease/recovery types are missing.

- [ ] **Step 3: Define guard and state APIs**

```csharp
public sealed record TaskbarRecoveryGuardRequest(
    string LeaseId,
    int OwnerProcessId,
    long OwnerProcessStartTimeUtcTicks,
    string JournalPath);

public sealed record TaskbarRecoveryGuardSession(int WatchdogProcessId);

public interface ITaskbarRecoveryGuard : IAsyncDisposable
{
    Task<TaskbarRecoveryGuardSession?> ArmAsync(
        TaskbarRecoveryGuardRequest request,
        TimeSpan readyTimeout,
        CancellationToken cancellationToken);
    Task DisarmAsync(string leaseId, TimeSpan exitTimeout, CancellationToken cancellationToken);
}

public enum TaskbarLeaseState { Released, Acquiring, Active, Releasing, RecoveryPending }

public interface ITaskbarLease : IAsyncDisposable
{
    TaskbarLeaseState State { get; }
    Task<bool> AcquireAsync(CancellationToken cancellationToken = default);
    Task<bool> ReconcileAsync(CancellationToken cancellationToken = default);
    Task<bool> ReleaseAsync(CancellationToken cancellationToken = default);
}

public enum ProcessIdentityStatus { Alive, NotAlive, Unknown }

public interface IProcessInspector
{
    ProcessIdentityStatus GetIdentityStatus(int processId, long processStartTimeUtcTicks);
}
```

`ProcessInspector.GetIdentityStatus` opens the PID and compares `StartTime.ToUniversalTime().Ticks`. It returns `NotAlive` only for a confirmed exited or reused PID, and `Unknown` for access/inspection failures. Recovery is allowed only for `NotAlive`; uncertainty fails closed. It never terminates or modifies a process.

Extend `TaskbarTestDoubles.cs` with `FakeTaskbarLeaseJournal`, `FakeTaskbarLeaseLock`, `FakeTaskbarRecoveryGuard`, and `LeaseHarness`. Each accepts the same shared event list used by `FakeTaskbarPlatform`, making the ordering assertions executable. The fake lock exposes ownership and rejects a second recovery/lease holder until its handle is disposed.

`RecoveryHarness.ActiveHiddenLease()` builds those fakes with an existing Active document and a currently hidden matching HWND. The fake journal retains an in-memory document plus `Exists`, while every fake exposes call counters used by the idempotency assertion.

- [ ] **Step 4: Implement the exact state sequence**

Use one private `SemaphoreSlim(1, 1)` for all public methods and release it in `finally`. Every awaited implementation call uses `ConfigureAwait(false)`. `AcquireAsync`: reject non-Released; acquire and retain the cross-process file lock; capture primary; create GUID lease; asynchronously arm guard; write Prepared. For an originally visible window, atomically rewrite its snapshot as `HidePending`, call hide, and inspect both the prior-visibility return and postcondition. Claim `HiddenByLease` only when the call observed it visible and the postcondition is hidden; a false prior-visibility return is an external-race conflict and becomes `Unchanged`. Write Active, then transition Active.

On failure after guard arm, restore only when identity matches and current visibility equals the lease-applied hidden state, delete journal after successful rollback, disarm, and return Released. If rollback itself cannot be verified, retain the journal and guard and transition to `RecoveryPending`; reject further acquire/reconcile attempts and let `ReleaseAsync` retry recovery.

`ReconcileAsync` runs only while Active. A new primary HWND is appended in a new generation and journaled before hide. If the new hide postcondition fails, record that generation as `Unchanged`; if the outcome is uncertain, retain `HidePending` and enter `RecoveryPending`. `ReleaseAsync` transitions Releasing, writes journal, restores modified snapshots in reverse, then deletes journal, asynchronously disarms, and releases the cross-process lock. Any restore failure keeps journal, guard, and file-lock handle owned in `RecoveryPending` so watchdog retries only after owner exit releases the OS handle.

- [ ] **Step 5: Implement shared recovery result**

```csharp
public sealed record TaskbarRecoveryResult(
    bool Succeeded,
    int RestoredCount,
    IReadOnlyList<long> FailedHandles,
    string? Error);

public interface ITaskbarRecoveryService
{
    Task<TaskbarRecoveryResult> TryRecoverAsync(
        string expectedLeaseId, CancellationToken cancellationToken = default);
    Task<TaskbarRecoveryResult> TryRecoverStaleAsync(
        CancellationToken cancellationToken = default);
}
```

`TryRecoverAsync(expectedLeaseId)` rejects a mismatched lease ID. `TryRecoverStaleAsync()` uses injected `IProcessInspector` and proceeds only when the recorded owner PID/start-time identity is confirmed `NotAlive`; `Alive` and `Unknown` both preserve the journal and fail closed. Both methods first acquire the shared cross-process file lock, revalidate each handle, restore only `HidePending`/`HiddenByLease` snapshots still in the applied hidden state, and delete the journal only after every eligible restore succeeds. Missing journal is an idempotent success. Corrupt or unsupported journals return a failed result without rewriting or deleting the source file.

There is no atomic transaction spanning a filesystem journal and Explorer's window state. The durable `HidePending` state prioritizes crash recovery: if the process dies in the tiny interval around `ShowWindow`, recovery restores the original visible state. While a lease is active, MacDock owns that exact taskbar window's visibility; an indistinguishable external request to keep the same window hidden cannot be preserved on release. The user explicitly approved this crash-recovery-first high-risk semantic on 2026-08-22.

`TaskbarLease.DisposeAsync` calls the same serialized release path. If recovery remains pending, it must retain the file-lock handle until process death; disposing local process/event wrappers must neither signal the stop event nor terminate the watchdog, because the child keeps its own handles and recovers after the OS releases the owner's file lock. Only a verified successful restore may disarm the guard and dispose the lock.

- [ ] **Step 6: Run green and race review**

Run the Task 3 command. Add blocking-fake tests proving: `Release` cannot race with `Reconcile`; cancellation/release prevents any later hide; repeated Shell-triggered reconciles serialize; Explorer replacement creates one new generation and never restores an invalid old HWND; a mismatched lease ID does nothing; and a failed recovery keeps the journal so a second attempt can succeed. Confirm no finalizer or background polling exists. Do not commit.

---

### Task 4: Watchdog Process and Handshake

**Files:**
- Create: `MacDock.Core/Services/Taskbar/TaskbarWatchdogClient.cs`
- Create: `MacDock.Core/Services/Taskbar/TaskbarWatchdogOptions.cs`
- Create: `MacDock.Core/Services/Taskbar/IWatchdogProcessLauncher.cs`
- Create: `MacDock.Core/Services/Taskbar/TaskbarWatchdogRunner.cs`
- Create: `MacDock.Watchdog/MacDock.Watchdog.csproj`
- Create: `MacDock.Watchdog/Program.cs`
- Modify: `MacDock.sln`
- Modify: `MacDock.UI/MacDock.UI.csproj`
- Test: `MacDock.Tests/WatchdogOptionsTests.cs`
- Test: `MacDock.Tests/TaskbarWatchdogClientTests.cs`
- Test: `MacDock.Tests/TaskbarWatchdogRunnerTests.cs`
- Modify: `MacDock.Tests/TaskbarTestDoubles.cs`

**Interfaces:**
- `TaskbarWatchdogClient` implements `ITaskbarRecoveryGuard`.
- `TaskbarWatchdogOptions` returns validated owner identity, lease ID, journal path, ready event, and stop event from Core, so tests do not reference the WinExe.
- `IWatchdogProcessLauncher` returns an injectable `IWatchdogProcess : IDisposable` with `Id`, `HasExited`, `Terminate()`, and `WaitForExit(TimeSpan)`; disposing the wrapper never terminates the OS process.
- `TaskbarWatchdogRunner` consumes a tiny fakeable runtime plus `ITaskbarRecoveryService`; `Program.cs` only validates arguments and composes production adapters.

- [ ] **Step 1: Write failing parsing and handshake tests**

```csharp
[Fact]
public void Parse_RejectsJournalOutsideMacDockAppData()
{
    var args = WatchdogSamples.ValidArgs()
        .WithJournal(@"C:\Windows\Temp\lease.json");
    Assert.False(TaskbarWatchdogOptions.TryParse(
        args, WatchdogSamples.AppDataRoot, out _, out _));
}

[Fact]
public async Task Arm_WhenReadyTimesOut_TerminatesChild()
{
    var process = new FakeWatchdogProcess { SignalsReady = false };
    var launcher = new FakeWatchdogProcessLauncher(process);
    await using var client = WatchdogClientHarness.Create(launcher);
    var session = await client.ArmAsync(
        WatchdogSamples.Request(), TimeSpan.FromMilliseconds(20), CancellationToken.None);
    Assert.Null(session);
    Assert.True(process.Terminated);
}

[Fact]
public async Task Runner_WhenOwnerExits_RecoversExpectedLease()
{
    var events = new List<string>();
    var runtime = FakeWatchdogRuntime.OwnerExitsAfterReady(events);
    var recovery = new FakeTaskbarRecoveryService(events, succeeds: true);

    var exitCode = await new TaskbarWatchdogRunner(recovery)
        .RunAsync(runtime, "11111111-1111-1111-1111-111111111111", CancellationToken.None);

    Assert.Equal(0, exitCode);
    Assert.Equal(
        new[] { "ready", "recover:11111111-1111-1111-1111-111111111111" },
        events);
}

[Fact]
public async Task Runner_WhenStopArrives_DoesNotRecover()
{
    var events = new List<string>();
    var runtime = FakeWatchdogRuntime.StopAfterReady(events);
    var recovery = new FakeTaskbarRecoveryService(events, succeeds: true);

    Assert.Equal(0, await new TaskbarWatchdogRunner(recovery)
        .RunAsync(runtime, "11111111-1111-1111-1111-111111111111", CancellationToken.None));
    Assert.Equal(0, recovery.Calls);
}
```

- [ ] **Step 2: Run red**

```powershell
dotnet test MacDock.Tests --filter "FullyQualifiedName~WatchdogOptionsTests|FullyQualifiedName~TaskbarWatchdogClientTests|FullyQualifiedName~TaskbarWatchdogRunnerTests" --no-restore
```

Expected: compile failure because watchdog types/project are absent.

Extend `TaskbarTestDoubles.cs` with `FakeWatchdogProcess`, `FakeWatchdogProcessLauncher`, `FakeWatchdogRuntime`, and `FakeTaskbarRecoveryService`. The launcher returns the fake process, parses the generated ready-event argument, and either opens/sets that real named event or intentionally withholds it; `Terminate()` records timeout cleanup without starting a child process. Runtime and recovery fakes share one event list and drive deterministic ready/stop/owner-exit sequences without starting a child.

`WatchdogSamples.ValidArgs()`, `AppDataRoot`, and `Request()` return fixed valid values beneath the test's unique temp AppData directory. `WatchdogClientHarness.Create(fakeLauncher)` injects that directory, the fake launcher, and a helper executable path string; it never requires the file to exist when the launcher is fake.

- [ ] **Step 3: Implement strict arguments**

Required pairs are `--parent-pid`, `--parent-start-ticks`, `--lease-id`, `--journal`, `--ready-event`, and `--stop-event`. Reject duplicates, omissions, extras, relative paths, malformed GUIDs, and any canonical journal path other than the exact current-user `%AppData%/MacDock/taskbar-lease.json`. Event names must use random `Local\\MacDock.Taskbar.<guid>.*` names generated by the parent; the helper never derives or accepts a global object name.

Create the helper project without additional packages:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows10.0.22621.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>12</LangVersion>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\MacDock.Core\MacDock.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Implement ready/stop protocol**

The client creates random named manual-reset events, starts `MacDock.Watchdog.exe` with `UseShellExecute = false`, and waits for ready on a worker task so the WPF Dispatcher is never blocked. Start failure, early child exit, cancellation, or timeout terminates the child and returns null. `DisarmAsync` signals stop, waits for a clean exit, and terminates only after its exit timeout. Plain disposal closes only the parent's local handles and never signals or kills an armed child.

The helper opens the owner process, verifies the exact PID/start time, opens events, constructs `TaskbarLeaseJournal`, `TaskbarLeaseFileLock`, `Win32TaskbarPlatform`, `TaskbarWindowService`, `ProcessInspector`, `TaskbarRecoveryService`, and `TaskbarWatchdogRunner`, then sets ready. An inaccessible/mismatched owner before ready is a conservative startup failure (no taskbar mutation can yet have occurred). After ready the runner executes:

```csharp
while (true)
{
    if (stopEvent.WaitOne(TimeSpan.FromMilliseconds(250)))
        return 0;
    if (owner.HasExited)
        return (await recovery.TryRecoverAsync(options.LeaseId)).Succeeded ? 0 : 3;
}
```

This loop watches parent lifetime only; it never re-hides or repeatedly enumerates taskbar windows. After owner exit, recovery waits for the shared file lock; whichever of watchdog or a newly started UI process acquires it first performs stale recovery, while the loser observes a missing/mismatched journal idempotently.

- [ ] **Step 5: Wire solution and output**

Add the watchdog project to `MacDock.sln`. Make UI build it as a non-reference dependency and add an AfterBuild copy target for `MacDock.Watchdog.exe`, `.dll`, `.deps.json`, `.runtimeconfig.json`, and `.pdb` into `$(OutDir)`. Fail the build if the executable is absent.

```xml
<ProjectReference Include="..\MacDock.Watchdog\MacDock.Watchdog.csproj"
                  ReferenceOutputAssembly="false" />

<Target Name="CopyWatchdogOutput" AfterTargets="Build">
  <ItemGroup>
    <WatchdogOutput Include="$(MSBuildProjectDirectory)\..\MacDock.Watchdog\bin\$(Configuration)\$(TargetFramework)\MacDock.Watchdog.*" />
  </ItemGroup>
  <Error Condition="!Exists('$(MSBuildProjectDirectory)\..\MacDock.Watchdog\bin\$(Configuration)\$(TargetFramework)\MacDock.Watchdog.exe')"
         Text="MacDock.Watchdog.exe was not built." />
  <Copy SourceFiles="@(WatchdogOutput)"
        DestinationFolder="$(OutDir)"
        SkipUnchangedFiles="true" />
</Target>
```

- [ ] **Step 6: Run green and verify output**

```powershell
dotnet restore MacDock.sln
dotnet test MacDock.Tests --filter "FullyQualifiedName~WatchdogOptionsTests|FullyQualifiedName~TaskbarWatchdogClientTests|FullyQualifiedName~TaskbarWatchdogRunnerTests" --no-restore
dotnet build MacDock.sln --no-restore
Test-Path 'MacDock.UI/bin/Debug/net8.0-windows10.0.22621.0/MacDock.Watchdog.exe'
```

Expected: tests pass, build exits zero, `Test-Path` is `True`. The client suite must also cover launcher exception, early child exit, cancellation, ready success, normal disarm, and disarm timeout; the runner suite covers stop, owner exit with recovery success/failure, and wrong lease rejection through the recovery fake. Do not launch either executable. Confirm no elevation, service, task-scheduler, or autostart registration. Do not commit.

---

### Task 5: Opt-In Atomic Settings

**Files:**
- Create: `MacDock.Core/Models/AppSettings.cs`
- Create: `MacDock.Core/Services/IAppSettingsStore.cs`
- Create: `MacDock.Core/Services/AppSettingsStore.cs`
- Test: `MacDock.Tests/AppSettingsStoreTests.cs`

**Interfaces:**
- Consumes `AtomicJsonFile<T>`.
- Produces injectable `IAppSettingsStore.Load()` and `Save(AppSettings)`.

- [ ] **Step 1: Write failing default/round-trip tests**

```csharp
[Fact]
public void Load_WhenMissing_DefaultsTaskbarTakeoverOff()
{
    var store = new AppSettingsStore(Path.Combine(_tempDirectory, "settings.json"));
    Assert.False(store.Load().HideWindowsTaskbar);
}

[Fact]
public void SaveThenLoad_RoundTripsTaskbarPreference()
{
    var store = new AppSettingsStore(Path.Combine(_tempDirectory, "settings.json"));
    store.Save(new AppSettings { HideWindowsTaskbar = true });
    Assert.True(store.Load().HideWindowsTaskbar);
}
```

- [ ] **Step 2: Run red**

```powershell
dotnet test MacDock.Tests --filter FullyQualifiedName~AppSettingsStoreTests --no-restore
```

- [ ] **Step 3: Implement model/store**

```csharp
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public bool HideWindowsTaskbar { get; set; } = false;
}

public interface IAppSettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}
```

Missing file returns defaults. Corrupt/unsupported JSON throws `InvalidDataException` and remains untouched. Save uses `AtomicJsonFile<AppSettings>`.

- [ ] **Step 4: Run green and review**

Run Task 5 tests. Confirm no test writes to real AppData and missing settings can never enable takeover. Do not commit.

---

### Task 6: App Ownership, Shell Messages, and Settings UI

**Files:**
- Create: `MacDock.Core/Services/Taskbar/TaskbarCoordinator.cs`
- Create: `MacDock.Core/Services/Taskbar/TaskbarStartupGate.cs`
- Create: `MacDock.Core/Services/Taskbar/ShellMessageClassifier.cs`
- Modify: `MacDock.UI/App.xaml.cs`
- Modify: `MacDock.UI/Views/DockWindow.xaml.cs`
- Modify: `MacDock.UI/ViewModels/MainViewModel.cs`
- Modify: `MacDock.UI/ViewModels/SettingsViewModel.cs`
- Modify: `MacDock.UI/Views/SettingsWindow.xaml`
- Modify: `MacDock.UI/Views/SettingsWindow.xaml.cs`
- Delete: `MacDock.Core/Services/TaskbarHider.cs`
- Test: `MacDock.Tests/TaskbarCoordinatorTests.cs`
- Test: `MacDock.Tests/TaskbarStartupGateTests.cs`
- Test: `MacDock.Tests/ShellMessageClassifierTests.cs`
- Test: `MacDock.Tests/SettingsViewModelTests.cs`
- Modify: `MacDock.Tests/MacDock.Tests.csproj`
- Modify: `MacDock.Tests/TaskbarTestDoubles.cs`

**Interfaces:**
- Consumes `ITaskbarLease`, `IAppSettingsStore`, and the already-loaded `AppSettings`; `App` composes recovery and watchdog into the lease before constructing the coordinator.
- Produces `SetEnabledAsync(bool)`, `ReconcileAsync()`, and `DisposeAsync()`.
- Coordinator does not call `Load()` itself, so startup can validate settings exactly once. It depends only on Core types, so its tests do not reference the WPF executable.
- `TaskbarStartupGate` serializes stale recovery before settings load and returns the initial settings, `changesAllowed`, and a readable error without touching WPF.

The design's “UI lifecycle adapter” responsibility remains in `App`; placing the side-effect-free coordinator in Core is a testability refinement, not permission for Core to reference WPF windows or Dispatcher APIs.

- [ ] **Step 1: Write failing coordinator tests**

```csharp
[Fact]
public async Task Enable_WhenAcquireFails_DoesNotPersistTrue()
{
    var harness = CoordinatorHarness.Create(acquireResult: false);
    var result = await harness.Coordinator.SetEnabledAsync(true);
    Assert.False(result.Succeeded);
    Assert.False(harness.Settings.Load().HideWindowsTaskbar);
}

[Fact]
public async Task Disable_ReleasesBeforeSavingFalse()
{
    var events = new List<string>();
    var harness = CoordinatorHarness.Create(events, true, true);
    await harness.Coordinator.SetEnabledAsync(true);
    await harness.Coordinator.SetEnabledAsync(false);
    Assert.True(events.IndexOf("release") < events.IndexOf("save-false"));
}

[Fact]
public async Task Dispose_ReleasesWithoutClearingPersistedOptIn()
{
    var harness = CoordinatorHarness.Create(acquireResult: true, releaseResult: true);
    await harness.Coordinator.SetEnabledAsync(true);

    await harness.Coordinator.DisposeAsync();

    Assert.True(harness.Settings.Load().HideWindowsTaskbar);
}

[Fact]
public async Task Startup_RecoversBeforeLoadingSettings()
{
    var events = new List<string>();
    var gate = StartupGateHarness.Create(events, recoverySucceeds: true);

    var result = await gate.PrepareAsync();

    Assert.True(result.ChangesAllowed);
    Assert.Equal(new[] { "recover-stale", "settings-load" }, events);
}

[Fact]
public async Task Startup_WhenRecoveryFails_BlocksPersistedEnable()
{
    var gate = StartupGateHarness.Create(
        recoverySucceeds: false, persistedTaskbarSetting: true);

    var result = await gate.PrepareAsync();

    Assert.False(result.ChangesAllowed);
    Assert.True(result.Settings.HideWindowsTaskbar);
    Assert.NotNull(result.Error);
}
```

- [ ] **Step 2: Run red**

```powershell
dotnet test MacDock.Tests --filter "FullyQualifiedName~TaskbarCoordinatorTests|FullyQualifiedName~TaskbarStartupGateTests|FullyQualifiedName~ShellMessageClassifierTests|FullyQualifiedName~SettingsViewModelTests" --no-restore
```

Expected: compilation fails because coordinator and classifier types are missing.

Extend `TaskbarTestDoubles.cs` with `FakeTaskbarLease : ITaskbarLease` and `FakeAppSettingsStore : IAppSettingsStore`; both append operations to a shared event list used by `CoordinatorHarness`.

`CoordinatorHarness.Create(...)` constructs the real Core `TaskbarCoordinator` with those two fakes plus an initial `AppSettings`, and initializes the fake settings store to `HideWindowsTaskbar = false`.

`StartupGateHarness` combines `FakeTaskbarRecoveryService` and `FakeAppSettingsStore`. `TaskbarStartupGate.PrepareAsync` always completes stale recovery before calling `Load`; failed recovery or invalid settings returns `ChangesAllowed = false`, preserves source files, and never rewrites the persisted preference.

- [ ] **Step 3: Implement coordinator**

```csharp
public sealed record TaskbarToggleResult(bool Succeeded, bool Enabled, string? Error);

public bool IsEnabled { get; }
public bool ChangesAllowed { get; }
public string? LastError { get; }
public Task<TaskbarToggleResult> SetEnabledAsync(
    bool enabled, CancellationToken cancellationToken = default);
public Task<bool> ReconcileAsync(CancellationToken cancellationToken = default);
public ValueTask DisposeAsync();
```

Enable saves true only after acquire succeeds. Disable saves false only after release succeeds. Reconcile is a no-op while disabled. The constructor accepts a `changesAllowed` flag plus an unavailable reason; startup recovery/settings failure passes false, and every acquire/reconcile attempt then fails closed without touching the lease. Coordinator serializes toggle and reconcile operations with its own `SemaphoreSlim`, owns the in-memory settings object, and never captures the WPF synchronization context.

`SetEnabledAsync` is idempotent. `DisposeAsync` releases an active lease but never saves `HideWindowsTaskbar = false`: normal application exit must preserve the user's opt-in for the next startup. A failed dispose leaves recovery to the armed watchdog.

- [ ] **Step 4: Move ownership to App**

`App.OnStartup`: acquire mutex and record whether it is owned; create taskbar dependencies; await `TaskbarStartupGate.PrepareAsync`; create/show DockWindow; after `SourceInitialized` asynchronously apply persisted true only when `ChangesAllowed`, with a top-level try/catch. Implement this through one observed `StartAsync` path because WPF's override itself cannot return `Task`. Startup recovery failure, a corrupt/unsupported journal, or unknown old-owner identity disables takeover for that run and preserves the journal. Any later startup failure disposes the coordinator before shutdown.

Construct UI dependencies explicitly:

```csharp
var mainViewModel = new MainViewModel();
_dockWindow = new DockWindow(
    mainViewModel,
    ShellMessageClassifier.CreateForCurrentProcess(),
    () => new SettingsViewModel(
        _taskbarCoordinator.IsEnabled,
        _taskbarCoordinator.ChangesAllowed,
        _taskbarCoordinator.LastError ?? _startupResult.Error,
        (enabled, token) => _taskbarCoordinator.SetEnabledAsync(enabled, token)));
_dockWindow.SourceInitialized += OnDockSourceInitialized;
_dockWindow.ShellEnvironmentChanged += OnShellEnvironmentChanged;
_dockWindow.Show();
```

Change constructors to `DockWindow(MainViewModel viewModel, ShellMessageClassifier shellMessages, Func<SettingsViewModel> settingsViewModelFactory)` and `SettingsWindow(SettingsViewModel viewModel)`. Store the factory in a readonly field, and change `OnSettingsClick` to `new SettingsWindow(_settingsViewModelFactory())`; there is no remaining parameterless settings-window call. `ShellMessageClassifier.CreateForCurrentProcess()` is the only production call that registers `TaskbarCreated`; tests construct it with a fixed message ID. Remove the `new MainViewModel()` field initializer from `DockWindow` so window construction has no hidden service creation.

The settings factory passes effective enabled state, availability/error state, and a `Func<bool, CancellationToken, Task<TaskbarToggleResult>>`; it preserves the existing `IsAutoStart` behavior. A blocked or failed startup is displayed unchecked even if the untouched persisted preference was true. If `settings.json` is corrupt or has an unsupported schema, preserve it, log the error, construct an in-memory default with takeover disabled, disable the taskbar checkbox for that run, and surface a readable error in the settings window; never silently enable takeover or overwrite the bad file. Add a UI project reference to `MacDock.Tests` solely for `SettingsViewModelTests`; loading that assembly must not construct `App`, a window, or a real taskbar service.

`App.OnExit`: stop owners, then use a bounded five-second synchronous bridge to `DisposeAsync().AsTask()`; all Core awaits use `ConfigureAwait(false)`, preventing a Dispatcher deadlock. Catch timeout and release exceptions, leaving the journal and watchdog armed for recovery. Call `ReleaseMutex()` only when owned, then dispose the mutex, and call `base.OnExit(e)` last. Do not depend on a finalizer. The UI-thread unhandled-exception path requests normal shutdown; the AppDomain fatal path only logs and lets the watchdog observe process death rather than calling WPF from an arbitrary thread.

```csharp
try
{
    var releaseTask = _taskbarCoordinator?.DisposeAsync().AsTask();
    if (releaseTask is not null && !releaseTask.Wait(TimeSpan.FromSeconds(5)))
        Logger.Error("任务栏租约释放超时，保留 journal 由 watchdog 恢复");
}
catch (Exception ex)
{
    Logger.Error(ex, "任务栏租约释放失败，保留 journal 由 watchdog 恢复");
}
```

- [ ] **Step 5: Forward Shell messages**

Install `HwndSource` hook in `DockWindow.OnSourceInitialized`, raise one `ShellEnvironmentChanged` event only for registered `TaskbarCreated` or `WM_DISPLAYCHANGE`, and remove the hook in `OnClosed`. The hook does not call Win32 taskbar methods.

The App event handler starts `ReconcileAsync` and observes it through a local async method with `try/catch`; do not use an unobserved fire-and-forget task.

`TaskbarStartupGateTests` additionally cover corrupt settings, corrupt/unsupported residual journal, exact-owner `Alive`/`Unknown`, and the case where a killed watchdog leaves a valid stale journal for next-start recovery. `TaskbarCoordinatorTests` cover duplicate toggles, reconcile bursts, disposal racing a queued reconcile, failed release, and startup `changesAllowed = false`.

- [ ] **Step 6: Remove unsafe ownership**

Remove only `_taskbarHider`, its constructor call, and disposal call from `MainViewModel`; preserve existing window-monitor/icon changes. Delete `TaskbarHider.cs`. Do not move registry, ABM, broad enumeration, finalizer, or polling code elsewhere.

- [ ] **Step 7: Add settings UI**

`SettingsViewModel` has manually-backed `HideWindowsTaskbar`, `IsTaskbarBusy`, `CanToggleTaskbar`, `TaskbarError`, and `IAsyncRelayCommand<bool?> SetTaskbarVisibilityCommand`. `CanToggleTaskbar` combines the immutable startup availability flag with `!IsTaskbarBusy`. The checkbox uses one-way state plus its command parameter, so no async property setter is required. The `IsTaskbarBusy` setter immediately raises `PropertyChanged` for both itself and `CanToggleTaskbar`; disable the checkbox before awaiting. On failure or exception keep the previous value, set a readable `TaskbarError`, and raise `PropertyChanged` so the visual check state reverts.

```csharp
SetTaskbarVisibilityCommand = new AsyncRelayCommand<bool?>(ApplyTaskbarVisibilityAsync);

private async Task ApplyTaskbarVisibilityAsync(
    bool? requested, CancellationToken cancellationToken)
{
    if (requested is null || IsTaskbarBusy)
        return;
    IsTaskbarBusy = true;
    TaskbarError = null;
    try
    {
        var result = await _setTaskbarEnabled(requested.Value, cancellationToken);
        if (result.Succeeded)
            SetProperty(ref _hideWindowsTaskbar, result.Enabled, nameof(HideWindowsTaskbar));
        else
            TaskbarError = result.Error;
        OnPropertyChanged(nameof(HideWindowsTaskbar));
    }
    catch (OperationCanceledException)
    {
        TaskbarError = "任务栏设置操作已取消";
        OnPropertyChanged(nameof(HideWindowsTaskbar));
    }
    catch (Exception ex)
    {
        TaskbarError = $"无法更改任务栏设置：{ex.Message}";
        OnPropertyChanged(nameof(HideWindowsTaskbar));
    }
    finally
    {
        IsTaskbarBusy = false;
    }
}
```

`SettingsViewModelTests` uses a controllable `TaskCompletionSource<TaskbarToggleResult>` to prove `CanToggleTaskbar` becomes false before the delegate completes, and proves a thrown delegate leaves the old check state and exposes `TaskbarError`.

```xml
<CheckBox IsChecked="{Binding HideWindowsTaskbar, Mode=OneWay}"
          IsEnabled="{Binding CanToggleTaskbar}"
          Command="{Binding SetTaskbarVisibilityCommand}"
          CommandParameter="{Binding RelativeSource={RelativeSource Self}, Path=IsChecked}"
          Content="隐藏 Windows 任务栏（主屏）"/>
<TextBlock Text="默认关闭；由独立恢复进程保护。仅接管主显示器。"
           TextWrapping="Wrap" Foreground="Gray" FontSize="12"/>
<TextBlock Text="{Binding TaskbarError}"
           Foreground="#C62828" TextWrapping="Wrap"/>
```

- [ ] **Step 8: Run integration checks without launching**

```powershell
dotnet test MacDock.Tests --no-restore
dotnet build MacDock.sln --no-restore
rg -n "StuckRects3|ABM_(GET|SET)STATE|SHAppBarMessage|ApplicationFrameHost|Shell_SecondaryTrayWnd|TrayButton|WM_SETTINGCHANGE|SendMessageTimeout|Reg(Open|Query|Set|Close)|QueueUserWorkItem|Thread\.Sleep\(3000\)|GetProcessesByName" MacDock.Core/Services/Taskbar MacDock.Core/Interop/NativeMethods.cs MacDock.UI/App.xaml.cs MacDock.UI/ViewModels/MainViewModel.cs MacDock.Watchdog
```

Expected: tests pass; build zero warnings/errors; `rg` has no production matches.

- [ ] **Step 9: Main-agent mixed-file review**

Compare every touched mixed file against HEAD and pre-task intent. Confirm no removal of `WindowMonitor`, running-dot, icon, or fish-eye work belonging to later batches. Do not commit mixed changes.

---

### Task 7: Safety Audit and Batch Acceptance

**Files:**
- Modify only if Tasks 1-6 contain an explicit reviewed defect.
- Update this plan's checkboxes after evidence is collected.

**Interfaces:**
- Produces an evidence-backed acceptance report; does not launch MacDock.

- [ ] **Step 1: Run focused tests**

```powershell
dotnet test MacDock.Tests --filter "FullyQualifiedName~Taskbar|FullyQualifiedName~Watchdog|FullyQualifiedName~NativeAbi|FullyQualifiedName~AppSettings" --no-restore
```

Expected: zero failed tests.

- [ ] **Step 2: Run full tests/build**

```powershell
dotnet test MacDock.Tests --no-restore
dotnet build MacDock.sln --no-restore
git diff --check
```

Expected: all tests pass, build has zero warnings/errors, diff check exits zero.

- [ ] **Step 3: Run static safety assertions**

```powershell
rg -n "StuckRects3|ABM_(GET|SET)STATE|SHAppBarMessage|ApplicationFrameHost|Shell_SecondaryTrayWnd|TrayButton|WM_SETTINGCHANGE|SendMessageTimeout|Reg(Open|Query|Set|Close)|QueueUserWorkItem|Thread\.Sleep\(3000\)|GetProcessesByName" MacDock.Core/Services/Taskbar MacDock.Core/Interop/NativeMethods.cs MacDock.UI/App.xaml.cs MacDock.UI/ViewModels/MainViewModel.cs MacDock.Watchdog
rg -n "Hide\(\)" MacDock.UI/ViewModels/MainViewModel.cs
git status --short --branch
```

Expected: no prohibited production matches; no taskbar hide in MainViewModel; branch remains main; unrelated pre-existing changes remain.

- [ ] **Step 4: Dry-run cleanup inventory only**

```powershell
git clean -ndX -d
git clean -ndx -d
```

Record output only. Never execute these clean commands. Actual `bin/obj` cleanup and `.gitignore` expansion occur after every repair batch is complete.

- [ ] **Step 5: Report and gate real integration**

Report automated evidence and state that real taskbar behavior was not exercised. Request separate permission before a disposable Windows VM Shell integration run.

---

## Deferred Project-Wide Cleanup (After All Repair Batches)

Do not perform this during the taskbar batch because later batches still need build artifacts and the SDD ledger. At final project acceptance:

- Extend root `.gitignore` only with unrelated generated/local artifacts: `.superpowers/`, `TestResults/`, `coverage/`, `*.trx`, `*.coverage`, `*.coveragexml`, `publish/`, `*.nupkg`, `*.snupkg`, `*.DotSettings.user`, `.DS_Store`, `Thumbs.db`, `Desktop.ini`, `*.swp`, `*.swo`, and `*~`.
- Do not broadly ignore `tools/`, `*.ps1`, `*.cs`, `.vscode/`, `.idea/`, `*.tmp`, or `*.json`; those can contain project source, diagnostics, or durable state contracts.
- After final verification, resolve and print every exact repository `bin`/`obj` target, assert each absolute path is beneath one of `D:\MacDock\MacDock.Core`, `MacDock.UI`, `MacDock.Animations`, `MacDock.Tests`, or `MacDock.Watchdog`, then remove those exact directories with PowerShell `Remove-Item -LiteralPath ... -Recurse -Force`.
- Delete only this plan's `D:\MacDock\.superpowers\sdd\2026-08-22-taskbar-safety` workspace after its final review; preserve sibling plan workspaces. Remove empty parents only after checking they contain nothing else.
- Never use `git clean -fdx`, `git clean -fdX`, a wildcard recursive delete, or cleanup against `%AppData%\MacDock`.
