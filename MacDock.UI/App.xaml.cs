using System.Windows;
using NLog;

namespace MacDock.UI;

/// <summary>
/// 应用程序入口：初始化日志与全局异常兜底，启动 Dock 主窗口。
/// </summary>
public partial class App : Application
{
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
}
