namespace MacDock.Core.Services.Taskbar;

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
