namespace MacDock.Core.Services.Taskbar;

public enum ProcessIdentityStatus
{
    Alive,
    NotAlive,
    Unknown,
}

public interface IProcessInspector
{
    ProcessIdentityStatus GetIdentityStatus(
        int processId,
        long processStartTimeUtcTicks);
}
