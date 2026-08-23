using System.Runtime.InteropServices;

namespace MacDock.Core.Services.Taskbar;

/// <summary>
/// Classifies only the shell messages that can invalidate the current taskbar lease.
/// </summary>
public sealed class ShellMessageClassifier
{
    public const uint WmDisplayChange = 0x007E;

    private const uint FirstRegisteredMessage = 0xC000;
    private const uint LastRegisteredMessage = 0xFFFF;
    private const string TaskbarCreatedMessageName = "TaskbarCreated";

    private readonly uint _taskbarCreatedMessage;

    public ShellMessageClassifier(uint taskbarCreatedMessage)
    {
        if (taskbarCreatedMessage == WmDisplayChange)
        {
            throw new ArgumentException(
                "The registered shell message cannot collide with WM_DISPLAYCHANGE.",
                nameof(taskbarCreatedMessage));
        }

        if (taskbarCreatedMessage is < FirstRegisteredMessage or > LastRegisteredMessage)
        {
            throw new ArgumentOutOfRangeException(
                nameof(taskbarCreatedMessage),
                "The registered shell message must be in the RegisterWindowMessage range.");
        }

        _taskbarCreatedMessage = taskbarCreatedMessage;
    }

    /// <summary>
    /// Registers the exact TaskbarCreated message for the current process.
    /// </summary>
    public static ShellMessageClassifier CreateForCurrentProcess()
    {
        var message = RegisterWindowMessageW(TaskbarCreatedMessageName);
        if (message == 0)
        {
            throw new InvalidOperationException(
                "Windows could not register the TaskbarCreated shell message.");
        }

        return new ShellMessageClassifier(message);
    }

    public bool IsShellEnvironmentChange(uint message)
        => message == _taskbarCreatedMessage || message == WmDisplayChange;

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Unicode,
        ExactSpelling = true,
        SetLastError = true)]
    private static extern uint RegisterWindowMessageW(string lpString);
}
