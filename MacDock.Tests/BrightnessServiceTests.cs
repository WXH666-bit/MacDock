using System.Diagnostics;
using MacDock.Core.Services;
using MacDock.UI.ViewModels;
using Xunit;

namespace MacDock.Tests;

/// <summary>
/// BrightnessService 逻辑单测：用假提供者验证可用性/读写/边界截断，
/// 不触碰真实 WMI（测试环境通常无亮度类，真实调用只会返回不可用）。</summary>
public sealed class BrightnessServiceTests
{
    private sealed class FakeProvider : IBrightnessProvider
    {
        public bool Available { get; set; } = true;

        public int? Brightness { get; set; } = 50;

        private int _setRequests;

        public int SetRequests => Volatile.Read(ref _setRequests);

        public bool IsAvailable() => Available;

        public int? GetBrightness() => Brightness;

        public bool SetBrightness(int level)
        {
            Interlocked.Increment(ref _setRequests);
            Brightness = level;
            return true;
        }
    }

    [Fact]
    public async Task IsAvailable_ReflectsProvider()
    {
        var available = new BrightnessService(new FakeProvider { Available = true });
        Assert.True(await available.IsAvailableAsync());
        available.Dispose();
        await available.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(2));

        var unavailable = new BrightnessService(new FakeProvider { Available = false });
        Assert.False(await unavailable.IsAvailableAsync());
        unavailable.Dispose();
        await unavailable.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task GetBrightness_ReturnsProviderValue()
    {
        var service = new BrightnessService(new FakeProvider { Brightness = 73 });
        Assert.Equal(73, await service.GetBrightnessAsync());
        service.Dispose();
        await service.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task SetBrightness_ClampsToRange()
    {
        var provider = new FakeProvider();
        var service = new BrightnessService(provider);

        Assert.True(await service.SetBrightnessAsync(120));
        Assert.Equal(100, provider.Brightness);

        Assert.True(await service.SetBrightnessAsync(-5));
        Assert.Equal(0, provider.Brightness);
        service.Dispose();
        await service.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task SetBrightness_RoundTrips()
    {
        var provider = new FakeProvider();
        var service = new BrightnessService(provider);

        Assert.True(await service.SetBrightnessAsync(65));
        Assert.Equal(65, provider.Brightness);
        Assert.Equal(65, await service.GetBrightnessAsync());
        service.Dispose();
        await service.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Unavailable_GetReturnsNull()
    {
        var service = new BrightnessService(new FakeProvider { Brightness = null });
        Assert.Null(await service.GetBrightnessAsync());
        service.Dispose();
        await service.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task GetBrightnessAsync_DoesNotBlockCallerWhenProviderIsSlow()
    {
        var provider = new BlockingReadProvider();
        var service = new BrightnessService(provider);

        var stopwatch = Stopwatch.StartNew();
        var readTask = service.GetBrightnessAsync();
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(500),
            $"调用方被同步阻塞了 {stopwatch.Elapsed.TotalMilliseconds:F0}ms");

        await provider.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        provider.AllowRead.TrySetResult(true);
        Assert.Equal(50, await readTask.WaitAsync(TimeSpan.FromSeconds(2)));

        service.Dispose();
        await service.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task BrightnessWorker_SerializesWrites()
    {
        var provider = new SerialWriteProvider();
        var service = new BrightnessService(provider);

        var first = service.SetBrightnessAsync(10);
        await provider.FirstWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = service.SetBrightnessAsync(20);

        provider.AllowFirstWrite.TrySetResult(true);

        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, provider.MaximumConcurrentWrites);
        Assert.Equal(new[] { 10, 20 }, provider.Values);

        service.Dispose();
        await service.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task WriteIsNotDroppedWhenReadQueueIsOccupied()
    {
        var provider = new PriorityProvider();
        var service = new BrightnessService(provider);

        var activeRead = service.GetBrightnessAsync();
        await provider.FirstReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var queuedRead = service.GetBrightnessAsync();
        var finalWrite = service.SetBrightnessAsync(88);

        provider.AllowFirstRead.TrySetResult(true);

        Assert.True(await finalWrite.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(new[] { 88 }, provider.Writes);
        Assert.Equal(50, await activeRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(50, await queuedRead.WaitAsync(TimeSpan.FromSeconds(2)));

        service.Dispose();
        await service.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Dispose_CompletesWorkerAndCancelsQueuedRead()
    {
        var provider = new BlockingReadProvider();
        var service = new BrightnessService(provider);

        _ = service.GetBrightnessAsync();
        await provider.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var queued = service.GetBrightnessAsync();

        service.Dispose();
        provider.AllowRead.TrySetResult(true);

        await service.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(await queued.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task CancelledQueuedWrite_DoesNotReachProvider()
    {
        var provider = new PriorityProvider();
        var service = new BrightnessService(provider);

        var activeRead = service.GetBrightnessAsync();
        await provider.FirstReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var cancellation = new CancellationTokenSource();
        var cancelledWrite = service.SetBrightnessAsync(77, cancellation.Token);
        cancellation.Cancel();
        Assert.False(await cancelledWrite.WaitAsync(TimeSpan.FromSeconds(2)));

        provider.AllowFirstRead.TrySetResult(true);
        Assert.Equal(50, await activeRead.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(await service.SetBrightnessAsync(99).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(new[] { 99 }, provider.Writes);

        service.Dispose();
        await service.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ProviderHardTimeout_CircuitBreaksWithoutAccumulatingCalls()
    {
        var provider = new HangingProvider();
        var service = new BrightnessService(
            provider,
            providerOperationTimeout: TimeSpan.FromMilliseconds(100));

        var first = service.GetBrightnessAsync();
        await provider.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(await first.WaitAsync(TimeSpan.FromSeconds(2)));
        await service.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Null(await service.GetBrightnessAsync().WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(1, provider.ReadCalls);

        provider.AllowRead.TrySetResult(true);
        service.Dispose();
    }

    [Fact]
    public async Task Dispose_StopsSupervisorEvenWhenProviderDoesNotReturn()
    {
        var provider = new HangingProvider();
        var service = new BrightnessService(
            provider,
            providerOperationTimeout: TimeSpan.FromSeconds(10));

        var read = service.GetBrightnessAsync();
        await provider.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        service.Dispose();
        await service.WorkerCompletion.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Null(await read.WaitAsync(TimeSpan.FromSeconds(1)));

        provider.AllowRead.TrySetResult(true);
    }

    [Fact]
    public async Task LatestValueWriter_Coalesces128UpdatesAndNeverRunsConcurrently()
    {
        var calls = new List<int>();
        var firstWriteEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var latestWriteCompleted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstWrite = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;

        var writer = new LatestValueAsyncWriter(
            async (value, cancellationToken) =>
            {
                var current = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, current);
                lock (calls)
                    calls.Add(value);

                if (value == 1)
                {
                    firstWriteEntered.TrySetResult(true);
                    await allowFirstWrite.Task.WaitAsync(cancellationToken);
                }

                if (value == 129)
                    latestWriteCompleted.TrySetResult(true);

                Interlocked.Decrement(ref active);
                return true;
            },
            TimeSpan.FromMilliseconds(10));

        writer.Enqueue(1);
        await firstWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (var value = 2; value <= 129; value++)
            writer.Enqueue(value);

        allowFirstWrite.TrySetResult(true);
        await latestWriteCompleted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        writer.Dispose();
        await writer.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        lock (calls)
        {
            Assert.True(calls.Count <= 2, $"实际写入次数为 {calls.Count}");
            Assert.Equal(129, calls[^1]);
        }

        Assert.Equal(1, maximumActive);
    }

    [Fact]
    public async Task LatestValueWriter_DisposeWithPendingSignalDoesNotThrow()
    {
        var writer = new LatestValueAsyncWriter(
            (_, _) => Task.FromResult(true),
            TimeSpan.FromSeconds(1));
        writer.Enqueue(42);

        var exception = Record.Exception(writer.Dispose);

        Assert.Null(exception);
        await writer.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task LatestValueWriter_DisposeFlushesPendingFinalValue()
    {
        var written = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new LatestValueAsyncWriter(
            (value, _) =>
            {
                written.TrySetResult(value);
                return Task.FromResult(true);
            },
            TimeSpan.FromSeconds(10));

        writer.Enqueue(83);
        writer.Dispose();

        Assert.Equal(83, await written.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        await writer.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task LatestValueWriter_AbortStopsAnUncooperativeWriteWait()
    {
        var writeEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var neverCompletes = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new LatestValueAsyncWriter(
            (_, _) =>
            {
                writeEntered.TrySetResult(true);
                return neverCompletes.Task;
            },
            TimeSpan.FromMilliseconds(10));

        writer.Enqueue(38);
        writer.Flush();
        await writeEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        writer.Dispose();
        writer.Abort();
        await writer.Completion.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task LatestValueWriter_FlushBypassesLongDebounce()
    {
        var written = new TaskCompletionSource<int>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new LatestValueAsyncWriter(
            (value, _) =>
            {
                written.TrySetResult(value);
                return Task.FromResult(true);
            },
            TimeSpan.FromSeconds(10));

        writer.Enqueue(64);
        await Task.Delay(50);
        Assert.False(written.Task.IsCompleted);

        writer.Flush();
        Assert.Equal(64, await written.Task.WaitAsync(TimeSpan.FromSeconds(1)));

        writer.Dispose();
        await writer.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task LatestValueWriter_RetriesFinalValueOnceAfterFailure()
    {
        var calls = 0;
        var completed = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new LatestValueAsyncWriter(
            (value, _) =>
            {
                Assert.Equal(71, value);
                if (Interlocked.Increment(ref calls) == 1)
                    return Task.FromResult(false);

                completed.TrySetResult(true);
                return Task.FromResult(true);
            },
            TimeSpan.FromMilliseconds(10));

        writer.Enqueue(71);
        writer.Flush();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, Volatile.Read(ref calls));

        writer.Dispose();
        await writer.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task LatestValueWriter_NewValueGetsItsOwnRetryBudget()
    {
        var firstValueFailed = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finalValueCompleted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<int>();
        var finalValueAttempts = 0;
        var writer = new LatestValueAsyncWriter(
            (value, _) =>
            {
                lock (calls)
                    calls.Add(value);

                if (value == 10)
                {
                    firstValueFailed.TrySetResult(true);
                    return Task.FromResult(false);
                }

                if (Interlocked.Increment(ref finalValueAttempts) == 1)
                    return Task.FromResult(false);

                finalValueCompleted.TrySetResult(true);
                return Task.FromResult(true);
            },
            TimeSpan.FromMilliseconds(10));

        writer.Enqueue(10);
        writer.Flush();
        await firstValueFailed.Task.WaitAsync(TimeSpan.FromSeconds(1));

        // 在值 10 的重试等待期间替换为值 20；20 应重新获得完整的两次尝试。
        writer.Enqueue(20);
        writer.Flush();
        await finalValueCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, Volatile.Read(ref finalValueAttempts));
        lock (calls)
            Assert.Equal(new[] { 10, 20, 20 }, calls);

        writer.Dispose();
        await writer.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (candidate <= current || Interlocked.CompareExchange(ref target, candidate, current) == current)
                return;
        }
    }

    private sealed class BlockingReadProvider : IBrightnessProvider
    {
        public TaskCompletionSource<bool> ReadEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> AllowRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsAvailable() => true;

        public int? GetBrightness()
        {
            ReadEntered.TrySetResult(true);
            AllowRead.Task.GetAwaiter().GetResult();
            return 50;
        }

        public bool SetBrightness(int level) => true;
    }

    private sealed class SerialWriteProvider : IBrightnessProvider
    {
        private int _activeWrites;
        private int _maximumConcurrentWrites;

        public TaskCompletionSource<bool> FirstWriteEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> AllowFirstWrite { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<int> Values { get; } = new();

        public int MaximumConcurrentWrites => Volatile.Read(ref _maximumConcurrentWrites);

        public bool IsAvailable() => true;

        public int? GetBrightness() => 50;

        public bool SetBrightness(int level)
        {
            var current = Interlocked.Increment(ref _activeWrites);
            UpdateMaximum(ref _maximumConcurrentWrites, current);
            lock (Values)
                Values.Add(level);

            if (level == 10)
            {
                FirstWriteEntered.TrySetResult(true);
                AllowFirstWrite.Task.GetAwaiter().GetResult();
            }

            Interlocked.Decrement(ref _activeWrites);
            return true;
        }
    }

    private sealed class PriorityProvider : IBrightnessProvider
    {
        private int _readCount;

        public TaskCompletionSource<bool> FirstReadEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> AllowFirstRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<int> Writes { get; } = new();

        public bool IsAvailable() => true;

        public int? GetBrightness()
        {
            if (Interlocked.Increment(ref _readCount) == 1)
            {
                FirstReadEntered.TrySetResult(true);
                AllowFirstRead.Task.GetAwaiter().GetResult();
            }

            return 50;
        }

        public bool SetBrightness(int level)
        {
            lock (Writes)
                Writes.Add(level);
            return true;
        }
    }

    private sealed class HangingProvider : IBrightnessProvider
    {
        private int _readCalls;

        public TaskCompletionSource<bool> ReadEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> AllowRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ReadCalls => Volatile.Read(ref _readCalls);

        public bool IsAvailable() => true;

        public int? GetBrightness()
        {
            Interlocked.Increment(ref _readCalls);
            ReadEntered.TrySetResult(true);
            AllowRead.Task.GetAwaiter().GetResult();
            return 50;
        }

        public bool SetBrightness(int level) => true;
    }
}
