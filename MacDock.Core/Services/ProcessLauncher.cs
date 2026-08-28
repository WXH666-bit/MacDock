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
    /// <summary>启动启动台中的桌面或商店应用。</summary>
    public static void Launch(InstalledApp app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (app.Kind == InstalledAppKind.Store)
        {
            if (string.IsNullOrWhiteSpace(app.Aumid))
                throw new InvalidDataException("商店应用缺少 AUMID。");

            StoreAppResolver.LaunchByAumid(app.Aumid);
            return;
        }

        Launch(new DockItem
        {
            Name = app.Name,
            Path = app.LaunchTarget,
            IconPath = app.IconPath,
            Arguments = app.Arguments,
        });
    }

    /// <summary>启动 Dock 项目对应的应用。</summary>
    public static void Launch(DockItem item)
    {
        // 商店应用项（无本地路径）：已运行则激活，否则解析 AUMID 拉起
        if (string.IsNullOrWhiteSpace(item.Path))
        {
            if (string.IsNullOrWhiteSpace(item.StoreAppName))
                return;

            if (TryActivateRunningInstance(item.StoreAppName))
                return;

            var storeAumid = StoreAppResolver.ResolveAumid(item.StoreAppName)
                ?? throw new FileNotFoundException($"未匹配到商店应用：{item.StoreAppName}");
            StoreAppResolver.LaunchByAumid(storeAumid);
            return;
        }

        var path = item.Path;

        // URI 协议（http/https、calculator: 等）：交给系统协议处理器，不做重复检测
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            StartDetached(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return;
        }

        var exeName = Path.GetFileNameWithoutExtension(path);

        // explorer.exe 特殊处理：始终打开新窗口（explorer 进程始终存在）
        if (string.Equals(exeName, "explorer", StringComparison.OrdinalIgnoreCase))
        {
            StartDetached(new ProcessStartInfo(path) { UseShellExecute = true });
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

        StartDetached(new ProcessStartInfo
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

        try
        {
            foreach (var process in processes)
            {
                try
                {
                    var handle = process.MainWindowHandle;
                    if (handle == IntPtr.Zero)
                        continue;

                    ForceToForeground(handle);
                    return true;
                }
                catch
                {
                    // 进程可能在枚举间隙退出，继续找下一个实例
                }
            }
        }
        finally
        {
            // 即使提前找到目标，也要释放数组里尚未遍历的 Process 句柄。
            foreach (var process in processes)
                process.Dispose();
        }

        return false;
    }

    private static void StartDetached(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo);
    }

    /// <summary>
    /// 强制把窗口置前台。Dock 带 WS_EX_NOACTIVATE，直接 SetForegroundWindow 会被
    /// 前台锁定策略静默拒绝；先 AttachThreadInput 挂靠前台线程的输入队列再激活。
    /// </summary>
    private static void ForceToForeground(IntPtr hwnd)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == hwnd)
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            return;
        }

        uint currentThread = NativeMethods.GetCurrentThreadId();
        uint foregroundThread = foreground != IntPtr.Zero
            ? NativeMethods.GetWindowThreadProcessId(foreground, out _)
            : 0;
        uint targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);

        bool attachForeground = foregroundThread != 0 && foregroundThread != currentThread;
        bool attachTarget = targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread;

        var foregroundAttached = false;
        var targetAttached = false;
        try
        {
            foregroundAttached = attachForeground
                && NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
            targetAttached = attachTarget
                && NativeMethods.AttachThreadInput(currentThread, targetThread, true);

            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            NativeMethods.BringWindowToTop(hwnd);
            NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (targetAttached)
                NativeMethods.AttachThreadInput(currentThread, targetThread, false);
            if (foregroundAttached)
                NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
        }
    }
}
