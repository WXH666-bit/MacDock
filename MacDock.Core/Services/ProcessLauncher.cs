using System.Diagnostics;
using MacDock.Core.Interop;
using MacDock.Core.Models;

namespace MacDock.Core.Services;

/// <summary>
/// 进程启动服务：支持本地 exe、URI 协议（http/https/calculator: 等）与商店应用；
/// 同一程序不重复启动——已运行时激活其主窗口到前台。
/// 启动失败抛出异常，由调用方（UI 层）负责日志与用户提示。
/// </summary>
public static class ProcessLauncher
{
    /// <summary>启动 Dock 项目对应的应用。</summary>
    public static void Launch(DockItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Path))
            return;

        var path = item.Path;

        // URI 协议（http/https、calculator: 等）：交给系统协议处理器，不做重复检测
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return;
        }

        var exeName = Path.GetFileNameWithoutExtension(path);

        // explorer.exe 特殊处理：始终打开新窗口（explorer 进程始终存在）
        if (string.Equals(exeName, "explorer", StringComparison.OrdinalIgnoreCase))
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return;
        }

        // 已运行：激活其主窗口到前台，而不是重复启动
        if (TryActivateRunningInstance(exeName))
            return;

        // 文件不存在：可能是商店应用（如 Win11 的计算器，System32\calc.exe 已失效）
        if (!File.Exists(path))
        {
            var aumid = StoreAppResolver.ResolveAumid(exeName);
            if (aumid is not null)
            {
                StoreAppResolver.LaunchByAumid(aumid);
                return;
            }

            throw new FileNotFoundException($"启动目标不存在，且未匹配到商店应用：{path}");
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            Arguments = item.Arguments ?? string.Empty,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty,
        });
    }

    /// <summary>找到指定进程名的可见主窗口并还原、置前台。</summary>
    private static bool TryActivateRunningInstance(string exeName)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(exeName);
        }
        catch
        {
            return false;
        }

        foreach (var process in processes)
        {
            try
            {
                var handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero)
                    continue;

                NativeMethods.ShowWindow(handle, NativeMethods.SW_RESTORE);
                NativeMethods.SetForegroundWindow(handle);
                return true;
            }
            catch
            {
                // 进程可能在枚举间隙退出，继续找下一个实例
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }
}
