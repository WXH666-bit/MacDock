using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using MacDock.Core;
using MacDock.Core.Services;
using MacDock.Core.Services.Taskbar;
using MacDock.UI.ViewModels;
using MacDock.UI.Views;
using NLog;

namespace MacDock.UI;

/// <summary>
/// 应用程序入口：单实例、启动恢复、任务栏租约和 Dock 窗口均由 App 统一拥有。
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Global\MacDock-SingleInstance";
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

    private static Mutex? _singleInstanceMutex;

    private bool _ownsSingleInstanceMutex;
    private bool _isExiting;
    private bool _persistedOptInApplied;
    private DockWindow? _dockWindow;
    private MenuBarWindow? _menuBarWindow;
    private TaskbarCoordinator? _taskbarCoordinator;
    private TaskbarStartupResult? _startupResult;
    private readonly CancellationTokenSource _startupCancellation = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            SingleInstanceMutexName,
            out var createdNew);
        _ownsSingleInstanceMutex = createdNew;

        if (!createdNew)
        {
            Logger.Warn("检测到已运行的 MacDock 实例，当前实例退出");
            ShowDuplicateInstanceBalloon();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        Logger.Info("MacDock 启动");

        // The wrapper observes StartAsync, including exceptions from its finally
        // cleanup. OnExit deliberately does not wait for this task.
        _ = ObserveStartupAsync(_startupCancellation.Token);
    }

    private async Task ObserveStartupAsync(CancellationToken cancellationToken)
    {
        try
        {
            await StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested || _isExiting)
        {
            // Cancellation is the expected shutdown path.
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "观察 MacDock 启动任务失败");
            if (!_isExiting)
                RequestShutdown();
        }
    }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        MainViewModel? mainViewModel = null;
        TaskbarLease? lease = null;
        TaskbarCoordinator? coordinator = null;
        DockWindow? dockWindow = null;
        var startupPublished = false;

        try
        {
            AppPaths.EnsureDataDirectory();

            var settingsStore = new AppSettingsStore(AppPaths.SettingsFile);
            var journal = new TaskbarLeaseJournal(AppPaths.TaskbarLeaseFile);
            var leaseLock = new TaskbarLeaseFileLock(AppPaths.TaskbarLeaseLockFile);
            var windowService = new TaskbarWindowService(new Win32TaskbarPlatform());
            var recoveryService = new TaskbarRecoveryService(
                windowService,
                journal,
                leaseLock,
                new ProcessInspector());

            var watchdogPath = Path.Combine(
                AppContext.BaseDirectory,
                "MacDock.Watchdog.exe");
            var recoveryGuard = new TaskbarWatchdogClient(
                watchdogPath,
                AppPaths.AppDataRoot,
                new WatchdogProcessLauncher());

            using var ownerProcess = Process.GetCurrentProcess();
            lease = new TaskbarLease(
                windowService,
                journal,
                leaseLock,
                recoveryGuard,
                ownerProcess.Id,
                ownerProcess.StartTime.ToUniversalTime().Ticks);

            var startupGate = new TaskbarStartupGate(recoveryService, settingsStore);
            var startupResult = await startupGate
                .PrepareAsync(cancellationToken)
                .ConfigureAwait(false);

            if (ShouldStopStartup(cancellationToken))
                return;

            await Dispatcher.InvokeAsync(() =>
            {
                if (ShouldStopStartup(cancellationToken))
                    return;

                _startupResult = startupResult;
                coordinator = new TaskbarCoordinator(
                    lease!,
                    settingsStore,
                    startupResult.Settings,
                    startupResult.ChangesAllowed,
                    startupResult.Error);
                lease = null; // ownership transferred to the local coordinator

                mainViewModel = new MainViewModel();
                var classifier = ShellMessageClassifier.CreateForCurrentProcess();
                dockWindow = new DockWindow(
                    mainViewModel,
                    classifier,
                    CreateSettingsViewModel);
                mainViewModel = null; // ownership transferred to DockWindow

                _taskbarCoordinator = coordinator;
                coordinator = null; // ownership transferred to App
                _dockWindow = dockWindow;
                dockWindow = null; // ownership transferred to App

                _dockWindow.SourceInitialized += OnDockSourceInitialized;
                _dockWindow.ShellEnvironmentChanged += OnShellEnvironmentChanged;
                _dockWindow.Show();

                // 菜单栏在 Dock 之后创建，复用 Dock 的 WindowMonitor（不重复挂 WinEventHook）。
                // 菜单栏失败不应连带 Dock 一起倒下，单独兜住异常。
                try
                {
                    _menuBarWindow = new MenuBarWindow(
                        new MenuBarViewModel(_dockWindow.WindowMonitor),
                        reserveWorkArea: startupResult.Settings.MenuBarReserveWorkArea,
                        trayTakeover: startupResult.Settings.TrayTakeover);
                    _menuBarWindow.Show();
                }
                catch (Exception menuBarException)
                {
                    Logger.Error(menuBarException, "创建顶部菜单栏失败，Dock 继续运行");
                    _menuBarWindow = null;
                }

                startupPublished = true;
            }).Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // OnExit cancels startup so no continuation can construct a window
            // after the mutex and taskbar ownership have begun shutting down.
        }
        catch (Exception exception)
        {
            if (!_isExiting)
            {
                Logger.Error(exception, "MacDock 启动失败");
                RequestShutdown();
            }
        }
        finally
        {
            // Recovery resumes off the UI dispatcher. Any startup rollback of a
            // WPF window must therefore marshal both handler detachment and
            // Close() back to the window's owning dispatcher.
            if (!startupPublished && _menuBarWindow is not null)
            {
                var failedMenuBar = _menuBarWindow;
                _menuBarWindow = null;
                await CloseWindowAsync(
                    failedMenuBar,
                    "启动失败后的菜单栏清理失败").ConfigureAwait(false);
            }

            if (!startupPublished && _dockWindow is not null)
            {
                var failedWindow = _dockWindow;
                _dockWindow = null;
                await CloseDockWindowAsync(
                    failedWindow,
                    "启动失败后的 Dock 清理失败").ConfigureAwait(false);
            }

            if (dockWindow is not null)
            {
                var pendingWindow = dockWindow;
                dockWindow = null;
                await CloseDockWindowAsync(
                    pendingWindow,
                    "未移交 Dock 的清理失败").ConfigureAwait(false);
            }

            if (!startupPublished && _taskbarCoordinator is not null)
            {
                var failedCoordinator = _taskbarCoordinator;
                _taskbarCoordinator = null;
                try
                {
                    await failedCoordinator.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception disposeException)
                {
                    Logger.Error(disposeException, "启动失败后的已发布 coordinator 清理失败");
                }
            }

            mainViewModel?.Dispose();

            if (coordinator is not null)
            {
                try
                {
                    await coordinator.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception disposeException)
                {
                    Logger.Error(disposeException, "启动失败后的 coordinator 清理失败");
                }
            }

            if (lease is not null)
            {
                try
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception disposeException)
                {
                    Logger.Error(disposeException, "启动失败后的任务栏租约清理失败");
                }
            }
        }
    }

    private async Task CloseDockWindowAsync(DockWindow window, string errorMessage)
    {
        try
        {
            if (window.Dispatcher.CheckAccess())
            {
                CloseDockWindowOnDispatcher(window);
                return;
            }

            await window.Dispatcher
                .InvokeAsync(() => CloseDockWindowOnDispatcher(window))
                .Task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, errorMessage);
        }
    }

    private void CloseDockWindowOnDispatcher(DockWindow window)
    {
        window.SourceInitialized -= OnDockSourceInitialized;
        window.ShellEnvironmentChanged -= OnShellEnvironmentChanged;
        window.Close();
    }

    /// <summary>在窗口自己的 dispatcher 上关闭窗口（菜单栏等无事件订阅的附属窗口）。</summary>
    private static async Task CloseWindowAsync(Window window, string errorMessage)
    {
        try
        {
            if (window.Dispatcher.CheckAccess())
            {
                window.Close();
                return;
            }

            await window.Dispatcher
                .InvokeAsync(window.Close)
                .Task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, errorMessage);
        }
    }

    private bool ShouldStopStartup(CancellationToken cancellationToken)
        => _isExiting || cancellationToken.IsCancellationRequested;

    private SettingsViewModel CreateSettingsViewModel()
    {
        var startup = _startupResult;
        var coordinator = _taskbarCoordinator;

        return new SettingsViewModel(
            initialTaskbarEnabled: coordinator?.IsEnabled ?? false,
            changesAllowed: startup?.ChangesAllowed ?? false,
            taskbarError: coordinator?.LastError
                ?? startup?.Error
                ?? "Taskbar startup is unavailable.",
            setTaskbarEnabled: (enabled, cancellationToken) => coordinator is null
                ? Task.FromResult(new TaskbarToggleResult(
                    Succeeded: false,
                    Enabled: false,
                    Error: "Taskbar coordinator is unavailable."))
                : coordinator.SetEnabledAsync(enabled, cancellationToken),
            readAutoStart: AutoStartService.IsEnabled,
            writeAutoStart: AutoStartService.SetEnabled);
    }

    private void OnDockSourceInitialized(object? sender, EventArgs e)
    {
        if (_persistedOptInApplied)
            return;

        _persistedOptInApplied = true;
        var startup = _startupResult;
        var coordinator = _taskbarCoordinator;
        if (startup is null
            || coordinator is null
            || !startup.ChangesAllowed
            || !startup.Settings.HideWindowsTaskbar)
        {
            return;
        }

        try
        {
            var operation = Dispatcher.InvokeAsync(
                () => ApplyPersistedTaskbarPreferenceAsync(coordinator),
                DispatcherPriority.Background);
            _ = ObserveQueuedTaskAsync(
                operation.Task,
                "应用已持久化的任务栏隐藏偏好时发生异常");
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "排队应用已持久化的任务栏隐藏偏好失败");
        }
    }

    private async Task ApplyPersistedTaskbarPreferenceAsync(
        TaskbarCoordinator coordinator)
    {
        try
        {
            if (_isExiting || !ReferenceEquals(coordinator, _taskbarCoordinator))
                return;

            var result = await coordinator.SetEnabledAsync(true).ConfigureAwait(false);
            if (!result.Succeeded)
                Logger.Warn("应用已持久化的任务栏隐藏偏好失败：{0}", result.Error);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "应用已持久化的任务栏隐藏偏好时发生异常");
        }
    }

    private void OnShellEnvironmentChanged(object? sender, EventArgs e)
    {
        if (_isExiting)
            return;

        try
        {
            var operation = Dispatcher.InvokeAsync(
                () => ReconcileTaskbarAsync(),
                DispatcherPriority.Background);
            _ = ObserveQueuedTaskAsync(
                operation.Task,
                "排队 Shell 环境变化后的任务栏 reconcile 失败");
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "排队 Shell 环境变化后的任务栏 reconcile 失败");
        }
    }

    private async Task ReconcileTaskbarAsync()
    {
        try
        {
            if (_isExiting)
                return;

            var coordinator = _taskbarCoordinator;
            if (_isExiting || coordinator is null)
                return;

            await coordinator.ReconcileAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "Shell 环境变化后的任务栏 reconcile 失败");
        }
    }

    private async Task ObserveQueuedTaskAsync(
        Task<Task> queuedTask,
        string errorMessage)
    {
        try
        {
            var callbackTask = await queuedTask.ConfigureAwait(false);
            await callbackTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, errorMessage);
        }
    }

    private void OnDispatcherUnhandledException(
        object? sender,
        DispatcherUnhandledExceptionEventArgs args)
    {
        Logger.Error(args.Exception, "未处理的 UI 线程异常");
        args.Handled = true;
        RequestShutdown();
    }

    private void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs args)
    {
        var exception = args.ExceptionObject as Exception;
        Logger.Error(exception, "未处理的 AppDomain 异常");
        // This callback may run on an arbitrary thread. The watchdog owns the
        // crash-recovery path; do not call WPF from here.
    }

    private void RequestShutdown()
    {
        try
        {
            if (Dispatcher.CheckAccess())
                Shutdown();
            else
                Dispatcher.BeginInvoke(Shutdown);
        }
        catch (Exception exception)
        {
            Logger.Error(exception, "请求 MacDock 退出失败");
        }
    }

    /// <summary>用临时托盘图标提示“已在运行”，气泡可见后退出。</summary>
    private void ShowDuplicateInstanceBalloon()
    {
        try
        {
            var tray = new TaskbarIcon
            {
                ToolTipText = "MacDock",
                Icon = System.Drawing.SystemIcons.Application,
            };
            tray.ShowNotification(
                "MacDock",
                "MacDock 已在运行中",
                NotificationIcon.Info,
                customIconHandle: null,
                largeIcon: false,
                sound: true,
                respectQuietTime: true,
                realtime: false,
                timeout: null);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                tray.Dispose();
                RequestShutdown();
            };
            timer.Start();
        }
        catch
        {
            RequestShutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _isExiting = true;
        try
        {
            _startupCancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A duplicate/defensive exit path may already have finalized startup.
        }
        catch (Exception exception)
        {
            // Cancellation callbacks are external code; never skip resource
            // cleanup if one of them throws during a defensive exit path.
            Logger.Error(exception, "取消 MacDock 启动任务失败");
        }

        if (_dockWindow is not null)
        {
            _dockWindow.SourceInitialized -= OnDockSourceInitialized;
            _dockWindow.ShellEnvironmentChanged -= OnShellEnvironmentChanged;
        }

        // 菜单栏随 Dock 一起退场，避免退出后残留置顶窗口
        if (_menuBarWindow is not null)
        {
            var menuBar = _menuBarWindow;
            _menuBarWindow = null;
            try
            {
                menuBar.Close();
            }
            catch (Exception exception)
            {
                Logger.Error(exception, "退出时关闭菜单栏失败");
            }
        }

        var coordinator = _taskbarCoordinator;
        if (coordinator is not null)
        {
            try
            {
                var disposal = coordinator.DisposeAsync().AsTask();
                if (!disposal.Wait(TimeSpan.FromSeconds(5)))
                {
                    Logger.Error("任务栏租约清理超过 5 秒，保留 journal/watchdog 恢复证据");
                }
                else
                {
                    disposal.GetAwaiter().GetResult();
                }
            }
            catch (Exception exception)
            {
                Logger.Error(exception, "退出时任务栏租约清理失败");
            }
        }

        if (_ownsSingleInstanceMutex && _singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch (Exception exception)
            {
                Logger.Error(exception, "释放单实例 mutex 失败");
            }
            finally
            {
                _ownsSingleInstanceMutex = false;
            }
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        try
        {
            _startupCancellation.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Dispose is intentionally idempotent for defensive exit paths.
        }
        base.OnExit(e);
    }
}
