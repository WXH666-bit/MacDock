using System.Windows;
using System.Windows.Threading;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using NLog;

namespace MacDock.UI;

/// <summary>
/// 应用程序入口：单实例检查、初始化日志与全局异常兜底，启动 Dock 主窗口。
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Global\MacDock-SingleInstance";
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();
    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例：已存在时提示气泡后退出，避免任务栏出现第二个 Dock
        _singleInstanceMutex = new Mutex(true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            Logger.Warn("检测到已运行的 MacDock 实例，当前实例退出");
            ShowDuplicateInstanceBalloon();
            return;
        }

        // 全局未处理异常兜底（M3 任务栏接管后需扩展恢复逻辑）
        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Error(args.Exception, "未处理的 UI 线程异常");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Logger.Error(args.ExceptionObject as Exception, "未处理的 AppDomain 异常");
        };

        Logger.Info("MacDock 启动");

        var dock = new Views.DockWindow();
        dock.Show();
    }

    /// <summary>用临时托盘图标提示"已在运行"，气泡可见后退出。</summary>
    private void ShowDuplicateInstanceBalloon()
    {
        try
        {
            var tray = new TaskbarIcon
            {
                ToolTipText = "MacDock",
                Icon = System.Drawing.SystemIcons.Application,
            };
            tray.ShowNotification("MacDock", "MacDock 已在运行中", NotificationIcon.Info,
                customIconHandle: null, largeIcon: false, sound: true,
                respectQuietTime: true, realtime: false, timeout: null);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                tray.Dispose();
                Shutdown();
            };
            timer.Start();
        }
        catch
        {
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        if (_singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 非持有线程调用时忽略
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
    }
}
