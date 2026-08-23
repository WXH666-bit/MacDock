using System.Diagnostics;
using MacDock.Core.Services.Taskbar;

namespace MacDock.Watchdog;

internal static class Program
{
    private static readonly TimeSpan OwnerPollInterval = TimeSpan.FromMilliseconds(250);

    public static async Task<int> Main(string[] args)
    {
        if (!TaskbarWatchdogOptions.TryParse(
                args,
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                out var options,
                out _)
            || options is null)
        {
            return TaskbarWatchdogRunner.RuntimeFailureExitCode;
        }

        Process? owner = null;
        EventWaitHandle? readyEvent = null;
        EventWaitHandle? stopEvent = null;
        try
        {
            var ownerProcess = Process.GetProcessById(options.ParentProcessId);
            owner = ownerProcess;
            if (!MatchesOwnerIdentity(ownerProcess, options))
                return TaskbarWatchdogRunner.RuntimeFailureExitCode;

            var readyHandle = EventWaitHandle.OpenExisting(options.ReadyEventName);
            readyEvent = readyHandle;
            var stopHandle = EventWaitHandle.OpenExisting(options.StopEventName);
            stopEvent = stopHandle;

            var journal = new TaskbarLeaseJournal(options.JournalPath);
            var journalDirectory = Path.GetDirectoryName(options.JournalPath)
                ?? throw new InvalidDataException("The journal directory is missing.");
            var leaseLock = new TaskbarLeaseFileLock(
                Path.Combine(journalDirectory, "taskbar-lease.lock"));
            var platform = new Win32TaskbarPlatform();
            var windowService = new TaskbarWindowService(platform);
            var processInspector = new ProcessInspector();
            var recoveryService = new TaskbarRecoveryService(
                windowService,
                journal,
                leaseLock,
                processInspector);
            var runtime = new TaskbarWatchdogRuntime(
                signalReady: () =>
                {
                    readyHandle.Set();
                },
                stopRequested: () => stopHandle.WaitOne(TimeSpan.Zero),
                ownerExited: () => ownerProcess.HasExited,
                pollInterval: OwnerPollInterval);

            return await new TaskbarWatchdogRunner(recoveryService)
                .RunAsync(runtime, options.LeaseId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            return TaskbarWatchdogRunner.RuntimeFailureExitCode;
        }
        finally
        {
            stopEvent?.Dispose();
            readyEvent?.Dispose();
            owner?.Dispose();
        }
    }

    private static bool MatchesOwnerIdentity(
        Process owner,
        TaskbarWatchdogOptions options)
    {
        if (owner.Id != options.ParentProcessId || owner.HasExited)
            return false;

        return owner.StartTime.ToUniversalTime().Ticks
            == options.ParentProcessStartTimeUtcTicks;
    }
}
