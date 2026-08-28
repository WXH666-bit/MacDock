using System.Windows.Threading;

namespace MacDock.Core.Services;

/// <summary>
/// 在独立 Dispatcher 线程上安装、承载并注销 WinEvent Hook。这样原生回调中的
/// 窗口枚举和 M4 屏幕快照不会占用 WPF UI 线程，同时保证 Hook 在安装线程注销。
/// </summary>
internal sealed class WinEventHookWorker : IDisposable
{
    private static readonly TimeSpan InitializationWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ShutdownWait = TimeSpan.FromSeconds(1);

    private readonly Action _initialize;
    private readonly Action _cleanup;
    private readonly Action<Exception>? _errorSink;
    private readonly ManualResetEventSlim _initialized = new(false);
    private readonly Thread _thread;
    private Dispatcher? _dispatcher;
    private int _disposeRequested;

    internal WinEventHookWorker(
        Action initialize,
        Action cleanup,
        Action<Exception>? errorSink = null)
    {
        _initialize = initialize ?? throw new ArgumentNullException(nameof(initialize));
        _cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));
        _errorSink = errorSink;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "MacDock.WinEventHook",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            Volatile.Write(ref _dispatcher, dispatcher);

            if (Volatile.Read(ref _disposeRequested) == 0)
                _initialize();

            _initialized.Set();
            if (Volatile.Read(ref _disposeRequested) == 0)
                Dispatcher.Run();
        }
        catch (Exception exception)
        {
            ReportError(exception);
        }
        finally
        {
            _initialized.Set();
            try
            {
                _cleanup();
            }
            catch (Exception exception)
            {
                ReportError(exception);
            }

            Volatile.Write(ref _dispatcher, null);
        }
    }

    private void ReportError(Exception exception)
    {
        try
        {
            _errorSink?.Invoke(exception);
        }
        catch
        {
            // 诊断回调不能破坏 Hook 线程的清理路径。
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
            return;

        if (Environment.CurrentManagedThreadId == _thread.ManagedThreadId)
        {
            Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            GC.SuppressFinalize(this);
            return;
        }

        if (!_initialized.Wait(InitializationWait))
        {
            ReportError(new TimeoutException("WinEvent Hook 线程初始化未在时限内结束。"));
        }

        var dispatcher = Volatile.Read(ref _dispatcher);
        if (dispatcher is not null && !dispatcher.HasShutdownStarted)
        {
            try
            {
                dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            }
            catch (InvalidOperationException exception)
            {
                ReportError(exception);
            }
        }

        if (!_thread.Join(ShutdownWait))
            ReportError(new TimeoutException("WinEvent Hook 线程未在时限内退出。"));

        GC.SuppressFinalize(this);
    }
}
