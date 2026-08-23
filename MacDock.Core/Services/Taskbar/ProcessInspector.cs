using System.Diagnostics;

namespace MacDock.Core.Services.Taskbar;

public sealed class ProcessInspector : IProcessInspector
{
    private readonly Func<int, long?> _startTicksProvider;

    public ProcessInspector()
        : this(GetStartTicks)
    {
    }

    internal ProcessInspector(Func<int, long?> startTicksProvider)
    {
        ArgumentNullException.ThrowIfNull(startTicksProvider);
        _startTicksProvider = startTicksProvider;
    }

    public ProcessIdentityStatus GetIdentityStatus(
        int processId,
        long processStartTimeUtcTicks)
    {
        if (processId <= 0 || processStartTimeUtcTicks <= 0)
            return ProcessIdentityStatus.Unknown;

        try
        {
            var actualStartTicks = _startTicksProvider(processId);
            if (actualStartTicks is null)
                return ProcessIdentityStatus.NotAlive;

            return actualStartTicks.Value == processStartTimeUtcTicks
                ? ProcessIdentityStatus.Alive
                : ProcessIdentityStatus.NotAlive;
        }
        catch (ArgumentException)
        {
            return ProcessIdentityStatus.NotAlive;
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            return ProcessIdentityStatus.Unknown;
        }
    }

    private static long? GetStartTicks(int processId)
    {
        using var process = Process.GetProcessById(processId);
        return process.StartTime.ToUniversalTime().Ticks;
    }
}
