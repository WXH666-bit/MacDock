using MacDock.Core.Interop;

namespace MacDock.Core.Services.Taskbar;

public sealed class TaskbarWindowService
{
    private const string PrimaryTaskbarClassName = "Shell_TrayWnd";
    private const string ExplorerProcessName = "explorer";

    private readonly ITaskbarPlatform _platform;

    public TaskbarWindowService(ITaskbarPlatform platform)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }

    public TaskbarWindowSnapshot? CapturePrimary()
    {
        try
        {
            var handle = _platform.FindWindow(PrimaryTaskbarClassName);
            if (handle == nint.Zero || !_platform.IsWindow(handle))
                return null;

            var className = _platform.GetWindowClassName(handle);
            if (!string.Equals(className, PrimaryTaskbarClassName, StringComparison.Ordinal))
                return null;

            var processId = _platform.GetWindowProcessId(handle);
            if (processId == 0
                || !string.Equals(_platform.GetProcessName(processId), ExplorerProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var processStartTicks = _platform.GetProcessStartTimeUtcTicks(processId);
            if (processStartTicks is not > 0)
                return null;

            var monitor = _platform.GetWindowMonitor(handle);
            var primaryMonitor = _platform.GetPrimaryMonitor();
            if (monitor == nint.Zero || primaryMonitor == nint.Zero || monitor != primaryMonitor)
                return null;

            var wasVisible = _platform.IsWindowVisible(handle);
            var showCommand = _platform.GetWindowShowCommand(handle);
            if (showCommand is null)
                return null;

            return new TaskbarWindowSnapshot(
                Handle: handle.ToInt64(),
                ProcessId: processId,
                ProcessStartTimeUtcTicks: processStartTicks.Value,
                ClassName: className!,
                MonitorHandle: monitor.ToInt64(),
                WasVisible: wasVisible,
                ShowCommand: showCommand.Value,
                MutationState: TaskbarWindowMutationState.Unchanged);
        }
        catch
        {
            return null;
        }
    }

    public bool TryHide(TaskbarWindowSnapshot snapshot)
        => TryHideDetailed(snapshot) == TaskbarHideOutcome.HiddenByLease;

    public TaskbarHideOutcome TryHideDetailed(TaskbarWindowSnapshot snapshot)
    {
        try
        {
            var handle = (nint)snapshot.Handle;
            if (MatchesIdentity(snapshot, handle) != TaskbarIdentityOutcome.Match
                || !IsOnCurrentPrimaryMonitor(handle))
            {
                return TaskbarHideOutcome.NotHidden;
            }
        }
        catch
        {
            return TaskbarHideOutcome.NotHidden;
        }

        try
        {
            var handle = (nint)snapshot.Handle;
            var wasVisibleBeforeCall = _platform.SetWindowShowState(handle, NativeMethods.SW_HIDE);
            if (MatchesIdentity(snapshot, handle) != TaskbarIdentityOutcome.Match)
                return TaskbarHideOutcome.Indeterminate;

            var isVisibleAfterCall = _platform.IsWindowVisible(handle);
            if (isVisibleAfterCall)
                return TaskbarHideOutcome.NotHidden;

            return wasVisibleBeforeCall
                ? TaskbarHideOutcome.HiddenByLease
                : TaskbarHideOutcome.AlreadyHidden;
        }
        catch
        {
            return TaskbarHideOutcome.Indeterminate;
        }
    }

    public bool TryRestore(TaskbarWindowSnapshot snapshot)
    {
        var outcome = TryRestoreDetailed(snapshot);
        return outcome is TaskbarRestoreOutcome.Restored
            or TaskbarRestoreOutcome.AlreadyVisible;
    }

    public TaskbarRestoreOutcome TryRestoreDetailed(TaskbarWindowSnapshot snapshot)
    {
        try
        {
            var handle = (nint)snapshot.Handle;
            var identity = MatchesIdentity(snapshot, handle);
            if (identity == TaskbarIdentityOutcome.Stale)
                return TaskbarRestoreOutcome.StaleIdentity;
            if (identity == TaskbarIdentityOutcome.Indeterminate)
                return TaskbarRestoreOutcome.Indeterminate;

            if (_platform.IsWindowVisible(handle))
                return TaskbarRestoreOutcome.AlreadyVisible;

            identity = MatchesIdentity(snapshot, handle);
            if (identity == TaskbarIdentityOutcome.Stale)
                return TaskbarRestoreOutcome.StaleIdentity;
            if (identity == TaskbarIdentityOutcome.Indeterminate)
                return TaskbarRestoreOutcome.Indeterminate;
        }
        catch
        {
            return TaskbarRestoreOutcome.Indeterminate;
        }

        try
        {
            var handle = (nint)snapshot.Handle;
            _platform.SetWindowShowState(handle, snapshot.ShowCommand);
            if (MatchesIdentity(snapshot, handle) != TaskbarIdentityOutcome.Match)
                return TaskbarRestoreOutcome.Indeterminate;

            return _platform.IsWindowVisible(handle)
                ? TaskbarRestoreOutcome.Restored
                : TaskbarRestoreOutcome.Failed;
        }
        catch
        {
            return TaskbarRestoreOutcome.Indeterminate;
        }
    }

    private TaskbarIdentityOutcome MatchesIdentity(TaskbarWindowSnapshot snapshot, nint handle)
    {
        if (handle == nint.Zero || !_platform.IsWindow(handle))
            return TaskbarIdentityOutcome.Stale;

        var className = _platform.GetWindowClassName(handle);
        if (className is null)
            return TaskbarIdentityOutcome.Indeterminate;
        if (!string.Equals(className, PrimaryTaskbarClassName, StringComparison.Ordinal)
            || !string.Equals(className, snapshot.ClassName, StringComparison.Ordinal))
        {
            return TaskbarIdentityOutcome.Stale;
        }

        var processId = _platform.GetWindowProcessId(handle);
        if (processId == 0)
            return TaskbarIdentityOutcome.Indeterminate;
        if (processId != snapshot.ProcessId)
            return TaskbarIdentityOutcome.Stale;

        var processStartTicks = _platform.GetProcessStartTimeUtcTicks(processId);
        if (processStartTicks is null)
            return TaskbarIdentityOutcome.Indeterminate;
        return processStartTicks == snapshot.ProcessStartTimeUtcTicks
            ? TaskbarIdentityOutcome.Match
            : TaskbarIdentityOutcome.Stale;
    }

    private bool IsOnCurrentPrimaryMonitor(nint handle)
    {
        var monitor = _platform.GetWindowMonitor(handle);
        var primaryMonitor = _platform.GetPrimaryMonitor();
        return monitor != nint.Zero && primaryMonitor != nint.Zero && monitor == primaryMonitor;
    }
}
