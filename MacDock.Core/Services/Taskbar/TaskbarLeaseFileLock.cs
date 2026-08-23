using System.Diagnostics;

namespace MacDock.Core.Services.Taskbar;

public sealed class TaskbarLeaseFileLock : ITaskbarLeaseLock
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    private readonly string _filePath;
    private readonly Func<Stopwatch, TimeSpan> _elapsedProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public TaskbarLeaseFileLock(string filePath)
        : this(
            filePath,
            static stopwatch => stopwatch.Elapsed,
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken))
    {
    }

    internal TaskbarLeaseFileLock(
        string filePath,
        Func<Stopwatch, TimeSpan> elapsedProvider,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("A lease lock path is required.", nameof(filePath));

        ArgumentNullException.ThrowIfNull(elapsedProvider);
        ArgumentNullException.ThrowIfNull(delayAsync);

        _filePath = filePath;
        _elapsedProvider = elapsedProvider;
        _delayAsync = delayAsync;
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeout);
        cancellationToken.ThrowIfCancellationRequested();

        var parentDirectory = Path.GetDirectoryName(Path.GetFullPath(_filePath));
        if (!string.IsNullOrEmpty(parentDirectory))
            Directory.CreateDirectory(parentDirectory);

        var stopwatch = Stopwatch.StartNew();
        var firstAttempt = true;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!firstAttempt
                && timeout != Timeout.InfiniteTimeSpan
                && _elapsedProvider(stopwatch) >= timeout)
            {
                return null;
            }

            firstAttempt = false;

            try
            {
                var stream = new FileStream(
                    _filePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    options: FileOptions.Asynchronous);

                if (cancellationToken.IsCancellationRequested)
                {
                    await stream.DisposeAsync();
                    cancellationToken.ThrowIfCancellationRequested();
                }

                return new LeaseHandle(stream);
            }
            catch (IOException exception) when (IsContention(exception))
            {
                if (timeout != Timeout.InfiniteTimeSpan)
                {
                    var remaining = timeout - _elapsedProvider(stopwatch);
                    if (remaining <= TimeSpan.Zero)
                        return null;

                    await _delayAsync(
                        remaining < PollInterval ? remaining : PollInterval,
                        cancellationToken);
                }
                else
                {
                    await _delayAsync(PollInterval, cancellationToken);
                }
            }
        }
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    private static bool IsContention(IOException exception)
    {
        var win32Error = exception.HResult & 0xFFFF;
        return win32Error is ErrorSharingViolation or ErrorLockViolation;
    }

    private sealed class LeaseHandle : IAsyncDisposable
    {
        private FileStream? _stream;

        public LeaseHandle(FileStream stream)
        {
            _stream = stream;
        }

        public ValueTask DisposeAsync()
        {
            var stream = Interlocked.Exchange(ref _stream, null);
            return stream?.DisposeAsync() ?? ValueTask.CompletedTask;
        }
    }
}
