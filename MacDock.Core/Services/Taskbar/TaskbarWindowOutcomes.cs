namespace MacDock.Core.Services.Taskbar;

public enum TaskbarHideOutcome
{
    HiddenByLease,
    AlreadyHidden,
    NotHidden,
    Indeterminate,
}

public enum TaskbarRestoreOutcome
{
    Restored,
    AlreadyVisible,
    StaleIdentity,
    Failed,
    Indeterminate,
}

internal enum TaskbarIdentityOutcome
{
    Match,
    Stale,
    Indeterminate,
}
