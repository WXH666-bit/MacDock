using System.Management;
using System.Threading.Channels;
using NLog;

namespace MacDock.Core.Services;

/// <summary>
/// 亮度控制提供者的抽象（真实实现走 WMI root\wmi），便于单测注入假实现。
/// 亮度范围 0-100；不可用（如台式机外接屏）时 IsAvailable() 返回 false。
/// </summary>
internal interface IBrightnessProvider
{
    /// <summary>当前环境是否支持亮度控制（WmiMonitorBrightness 类可查询）。</summary>
    bool IsAvailable();

    /// <summary>读取当前亮度（0-100）。失败返回 null。</summary>
    int? GetBrightness();

    /// <summary>设置亮度（0-100）。返回是否成功。</summary>
    bool SetBrightness(int level);
}

/// <summary>
/// 亮度控制服务：读写内屏亮度（WMI，覆盖 Windows 标准亮度接口，无厂商专用驱动）。
/// 供菜单栏太阳图标、浮窗滑条与滚轮步进使用。
/// 所有 WMI 操作由单个有界后台 worker 串行执行；任何失败都不抛异常。
/// WMI 同时使用 System.Management 原生超时和服务级硬截止时间；底层 COM 若无视超时，
/// 服务会立即熔断，最多遗留一个后台调用，后续请求不会继续堆积。
/// </summary>
public sealed class BrightnessService
    : IDisposable
{
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>写请求等待队列槽位的上限；超时后只返回失败，不留下等待者。</summary>
    private static readonly TimeSpan QueueWaitTimeout = TimeSpan.FromSeconds(2);

    /// <summary>覆盖 WMI 隐式连接、元数据读取等不接受原生 Timeout 的阶段。</summary>
    private static readonly TimeSpan DefaultProviderOperationTimeout = TimeSpan.FromSeconds(2);

    private readonly IBrightnessProvider _provider;
    private readonly TimeSpan _providerOperationTimeout;
    private readonly CancellationTokenSource _shutdown = new();
    // 读写分离：读请求不能占满写请求的唯一待处理槽位；worker 始终优先排空写通道。
    private readonly Channel<IBrightnessOperation> _writeOperations = CreateOperationChannel();
    private readonly Channel<IBrightnessOperation> _readOperations = CreateOperationChannel();
    private readonly Task _worker;
    private int _disposed;
    private int _providerTimedOut;

    public BrightnessService() : this(new WmiBrightnessProvider())
    {
    }

    /// <summary>供单测注入假提供者。</summary>
    internal BrightnessService(
        IBrightnessProvider provider,
        TimeSpan? providerOperationTimeout = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _providerOperationTimeout = providerOperationTimeout ?? DefaultProviderOperationTimeout;
        if (_providerOperationTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(providerOperationTimeout));

        _worker = Task.Run(ProcessOperationsAsync);
    }

    /// <summary>异步查询当前环境是否支持亮度控制。</summary>
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        => EnqueueRead(provider => provider.IsAvailable(), cancellationToken);

    /// <summary>异步读取当前亮度（0-100）。失败返回 null。</summary>
    public Task<int?> GetBrightnessAsync(CancellationToken cancellationToken = default)
        => EnqueueRead(provider => provider.GetBrightness(), cancellationToken);

    /// <summary>异步设置亮度（0-100，超出范围会被截断）。失败返回 false。</summary>
    public Task<bool> SetBrightnessAsync(int level, CancellationToken cancellationToken = default)
    {
        level = Math.Clamp(level, 0, 100);
        return EnqueueWrite(provider => provider.SetBrightness(level), cancellationToken);
    }

    /// <summary>供测试等待 worker 在 Dispose 后确实退出；生产调用方不需要使用。</summary>
    internal Task WorkerCompletion => _worker;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // 不等待正在进行的 WMI 调用：Dispose 可能发生在 UI 线程。
        // shutdown 会让监督 worker 立即退出；底层 COM 若卡死也只会遗留这一个调用。
        _shutdown.Cancel();
        _writeOperations.Writer.TryComplete();
        _readOperations.Writer.TryComplete();
    }

    private static Channel<IBrightnessOperation> CreateOperationChannel()
        => Channel.CreateBounded<IBrightnessOperation>(new BoundedChannelOptions(1)
        {
            // 每条通道最多一个待处理操作；写入方不能无限积压。
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

    private Task<T> EnqueueRead<T>(
        Func<IBrightnessProvider, T> action,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested
            || Volatile.Read(ref _disposed) != 0
            || Volatile.Read(ref _providerTimedOut) != 0)
            return Task.FromResult(default(T)!);

        var operation = new BrightnessOperation<T>(action);
        // 读请求允许在读槽位忙时跳过；低频轮询下一轮会再次读取，不能阻塞 UI。
        if (!_readOperations.Writer.TryWrite(operation))
            return Task.FromResult(default(T)!);

        return AwaitOperationAsync(operation, cancellationToken);
    }

    private async Task<T> EnqueueWrite<T>(
        Func<IBrightnessProvider, T> action,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested
            || Volatile.Read(ref _disposed) != 0
            || Volatile.Read(ref _providerTimedOut) != 0)
            return default!;

        var operation = new BrightnessOperation<T>(action);
        using var queueTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        queueTimeout.CancelAfter(QueueWaitTimeout);

        try
        {
            // 写请求有自己的有界优先级通道；若前一个写尚未出槽，只等待有限时间。
            // 取消会撤销正在 WriteAsync 中的等待，不会留下稍后又偷偷入队的 waiter。
            await _writeOperations.Writer.WriteAsync(operation, queueTimeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            operation.Cancel();
            return default!;
        }
        catch (ChannelClosedException)
        {
            operation.Cancel();
            return default!;
        }

        return await AwaitOperationAsync(operation, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> AwaitOperationAsync<T>(
        BrightnessOperation<T> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 若操作仍在队列中，worker 看到终止状态后不会再调用 provider；若已进入
            // 不可取消的 WMI/COM 调用，则只让调用方停止等待，由监督 worker 负责收敛。
            operation.Cancel();
            return default!;
        }
    }

    private async Task ProcessOperationsAsync()
    {
        Task<bool>? writeReady = null;
        Task<bool>? readReady = null;
        var writesCompleted = false;
        var readsCompleted = false;

        try
        {
            while (true)
            {
                while (_writeOperations.Reader.TryRead(out var writeOperation))
                {
                    if (!await ExecuteOrCancelAsync(writeOperation).ConfigureAwait(false))
                        return;
                }

                while (_readOperations.Reader.TryRead(out var readOperation))
                {
                    if (!await ExecuteOrCancelAsync(readOperation).ConfigureAwait(false))
                        return;
                }

                if (writesCompleted && readsCompleted)
                    break;

                writeReady ??= writesCompleted
                    ? null
                    : _writeOperations.Reader.WaitToReadAsync().AsTask();
                readReady ??= readsCompleted
                    ? null
                    : _readOperations.Reader.WaitToReadAsync().AsTask();

                if (writeReady is not null && readReady is not null)
                    await Task.WhenAny(writeReady, readReady).ConfigureAwait(false);
                else if (writeReady is not null)
                    await writeReady.ConfigureAwait(false);
                else if (readReady is not null)
                    await readReady.ConfigureAwait(false);

                if (writeReady?.IsCompleted == true)
                {
                    writesCompleted = !await writeReady.ConfigureAwait(false);
                    writeReady = null;
                }

                if (readReady?.IsCompleted == true)
                {
                    readsCompleted = !await readReady.ConfigureAwait(false);
                    readReady = null;
                }
            }
        }
        catch (Exception exception)
        {
            // 防止 worker 异常变成未观察异常；当前操作/排队操作都会在 finally 中收敛。
            Logger.Error(exception, "亮度 WMI worker 异常退出");
        }
        finally
        {
            while (_writeOperations.Reader.TryRead(out var writeOperation))
                writeOperation.Cancel();
            while (_readOperations.Reader.TryRead(out var readOperation))
                readOperation.Cancel();
        }
    }

    private async Task<bool> ExecuteOrCancelAsync(IBrightnessOperation operation)
    {
        if (Volatile.Read(ref _disposed) != 0
            || Volatile.Read(ref _providerTimedOut) != 0)
        {
            operation.Cancel();
            return Volatile.Read(ref _disposed) == 0;
        }

        // System.Management 的 Timeout 不覆盖隐式 ConnectServer 和部分元数据读取。
        // 在单个受监督任务中执行 provider；若硬截止时间到达便永久熔断本服务实例，
        // 因而即使 COM 永久不返回，也不会每次轮询再遗留一个线程。
        var execution = Task.Run(() => operation.Execute(_provider));
        try
        {
            await execution
                .WaitAsync(_providerOperationTimeout, _shutdown.Token)
                .ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            operation.Cancel();
            return false;
        }
        catch (TimeoutException)
        {
            operation.Cancel();
            if (Interlocked.Exchange(ref _providerTimedOut, 1) == 0)
            {
                Logger.Error(
                    "亮度 WMI 操作超过硬截止时间 {0}ms，本次运行停止后续亮度访问",
                    _providerOperationTimeout.TotalMilliseconds);
            }

            _writeOperations.Writer.TryComplete();
            _readOperations.Writer.TryComplete();
            return false;
        }
    }

    private interface IBrightnessOperation
    {
        void Execute(IBrightnessProvider provider);

        void Cancel();
    }

    private sealed class BrightnessOperation<T> : IBrightnessOperation
    {
        private static readonly ILogger OperationLogger = LogManager.GetCurrentClassLogger();

        private readonly Func<IBrightnessProvider, T> _action;
        private readonly TaskCompletionSource<T> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        // 0=pending, 1=executing, 2=completed/cancelled。取消队列项后 Execute 不得再碰 WMI。
        private int _state;

        public BrightnessOperation(Func<IBrightnessProvider, T> action)
        {
            _action = action;
        }

        public Task<T> Completion => _completion.Task;

        public void Execute(IBrightnessProvider provider)
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
                return;

            try
            {
                _completion.TrySetResult(_action(provider));
            }
            catch (Exception exception)
            {
                OperationLogger.Warn(exception, "亮度 WMI 操作失败");
                _completion.TrySetResult(default!);
            }
            finally
            {
                Volatile.Write(ref _state, 2);
            }
        }

        public void Cancel()
        {
            Interlocked.Exchange(ref _state, 2);
            _completion.TrySetResult(default!);
        }
    }
}

/// <summary>
/// WMI 实现：WmiMonitorBrightness 读当前亮度，WmiMonitorBrightnessMethods.WmiSetBrightness 写。
/// 查询不到类（台式机/外接屏常见）时 IsAvailable() 返回 false。
/// </summary>
internal sealed class WmiBrightnessProvider : IBrightnessProvider
{
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(1);

    private const string NamespacePath = @"root\wmi";
    private const string InstanceQuery = "SELECT CurrentBrightness FROM WmiMonitorBrightness";
    private const string MethodsQuery = "SELECT * FROM WmiMonitorBrightnessMethods";
    private const string MethodName = "WmiSetBrightness";

    /// <summary>首查失败后重试的 TTL（避免把一次瞬时失败永久当成「不支持」）。</summary>
    private static readonly TimeSpan UnavailableRetryTtl = TimeSpan.FromSeconds(60);

    private readonly object _sync = new();
    private bool? _available;
    private DateTime _availableFetchedAt;

    public bool IsAvailable()
    {
        lock (_sync)
        {
            // 成功可长期缓存；失败短暂缓存，过 60 秒到期重试（台式机可能热插显示器）
            if (_available.HasValue
                && (_available.Value || DateTime.UtcNow - _availableFetchedAt < UnavailableRetryTtl))
            {
                return _available.Value;
            }

            _available = QueryAvailable();
            _availableFetchedAt = DateTime.UtcNow;
            return _available.Value;
        }
    }

    public int? GetBrightness()
    {
        try
        {
            using var searcher = CreateSearcher(InstanceQuery);
            using var results = searcher.Get();
            foreach (ManagementBaseObject obj in results)
            {
                using (obj)
                {
                    // 可取到 CurrentBrightness 属性即认为有效内屏
                    if (obj.GetPropertyValue("CurrentBrightness") is byte brightness)
                        return brightness;
                }
            }

            return null;
        }
        catch (Exception exception)
        {
            Logger.Warn(exception, "读取亮度失败（可能不支持亮度控制）");
            return null;
        }
    }

    public bool SetBrightness(int level)
    {
        try
        {
            // WmiSetBrightness 必须在 WmiMonitorBrightnessMethods 的实例上调用。
            // 契约要求 Brightness 为 uint8、Timeout 为 uint32；不能把整个 class 当作实例调用。
            using var searcher = CreateSearcher(MethodsQuery);
            using var instances = searcher.Get();

            foreach (ManagementObject methodInstance in instances)
            {
                using (methodInstance)
                {
                    // GetMethodParameters 会按 ObjectGetOptions 读取方法元数据；默认 Timeout
                    // 是无限，因此必须给实例本身也配置超时。
                    methodInstance.Options = new ObjectGetOptions
                    {
                        Timeout = OperationTimeout,
                    };
                    using var inParams = methodInstance.GetMethodParameters(MethodName);
                    inParams["Brightness"] = (byte)Math.Clamp(level, 0, 100);
                    inParams["Timeout"] = 1u;

                    var options = new InvokeMethodOptions
                    {
                        Timeout = OperationTimeout,
                    };
                    using var result = methodInstance.InvokeMethod(MethodName, inParams, options);
                    // WMI 调用返回对象不等于方法成功；ReturnValue 非 0 必须视为失败。
                    return result?.Properties["ReturnValue"]?.Value is uint returnValue
                        && returnValue == 0;
                }
            }

            return false;
        }
        catch (Exception exception)
        {
            Logger.Warn(exception, "设置亮度失败");
            return false;
        }
    }

    /// <summary>尝试建立一次查询判断类是否存在；异常/空结果视为不可用。</summary>
    private static bool QueryAvailable()
    {
        try
        {
            using var searcher = CreateSearcher(InstanceQuery);
            using var results = searcher.Get();
            foreach (ManagementBaseObject obj in results)
            {
                obj.Dispose();
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static System.Management.EnumerationOptions CreateEnumerationOptions()
        => new()
        {
            ReturnImmediately = true,
            Rewindable = false,
            Timeout = OperationTimeout,
        };

    private static ManagementObjectSearcher CreateSearcher(string query)
    {
        // 显式 scope 让可配置的连接路径也带上原生 Timeout；System.Management
        // 仍有不受该值控制的 COM 阶段，BrightnessService 的硬截止时间负责最终兜底。
        var scope = new ManagementScope(
            NamespacePath,
            new ConnectionOptions
            {
                Timeout = OperationTimeout,
            });
        return new ManagementObjectSearcher(
            scope,
            new ObjectQuery(query),
            CreateEnumerationOptions());
    }
}
