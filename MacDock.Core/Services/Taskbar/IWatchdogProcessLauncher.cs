using System.Diagnostics;

namespace MacDock.Core.Services.Taskbar;

public interface IWatchdogProcessLauncher
{
    IWatchdogProcess Start(
        string executablePath,
        IReadOnlyList<string> arguments);
}

public interface IWatchdogProcess : IDisposable
{
    int Id { get; }

    bool HasExited { get; }

    void Terminate();

    bool WaitForExit(TimeSpan timeout);
}

public sealed class WatchdogProcessLauncher : IWatchdogProcessLauncher
{
    public IWatchdogProcess Start(
        string executablePath,
        IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            if (argument is null)
                throw new ArgumentException("Watchdog arguments cannot be null.", nameof(arguments));

            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The watchdog process could not be started.");
        return new WatchdogProcess(process);
    }

    private sealed class WatchdogProcess : IWatchdogProcess
    {
        private Process? _process;

        public WatchdogProcess(Process process)
        {
            _process = process ?? throw new ArgumentNullException(nameof(process));
        }

        public int Id
            => GetProcess().Id;

        public bool HasExited
            => GetProcess().HasExited;

        public void Terminate()
        {
            var process = Volatile.Read(ref _process);
            if (process is null)
                return;

            if (!process.HasExited)
                process.Kill(entireProcessTree: false);
        }

        public bool WaitForExit(TimeSpan timeout)
        {
            ValidateTimeout(timeout);
            var process = GetProcess();
            if (timeout == Timeout.InfiniteTimeSpan)
            {
                process.WaitForExit();
                return true;
            }

            var milliseconds = timeout.TotalMilliseconds >= int.MaxValue
                ? int.MaxValue
                : (int)Math.Ceiling(timeout.TotalMilliseconds);
            return process.WaitForExit(milliseconds);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _process, null)?.Dispose();
        }

        private Process GetProcess()
            => Volatile.Read(ref _process)
                ?? throw new ObjectDisposedException(nameof(WatchdogProcess));

        private static void ValidateTimeout(TimeSpan timeout)
        {
            if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }
}
