using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MacDock.Core.Interop;
using NLog;

namespace MacDock.Core.Services;

/// <summary>
/// 窗口监控服务：通过 SetWinEventHook 监听系统窗口事件（前台切换/最小化/还原/显示/销毁），
/// 维护当前具有可见顶层窗口的进程集合，供 Dock 显示运行状态小圆点。
/// </summary>
public sealed class WindowMonitor : IDisposable
{
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>有可见顶层窗口的进程名集合（小写，不含扩展名）。</summary>
    private readonly HashSet<string> _visibleProcesses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>被排除的进程名（explorer 等始终存在的系统进程）。</summary>
    private static readonly HashSet<string> ExcludedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",
        "SearchIndexer",
        "dwm",
        "csrss",
        "wininit",
        "winlogon",
        "services",
        "lsass",
        "svchost",
    };

    /// <summary>WinEventHook 句柄列表。</summary>
    private readonly List<IntPtr> _hookHandles = new();

    /// <summary>WinEventHook 回调委托（必须保持引用，否则被 GC 回收）。</summary>
    private readonly NativeMethods.WinEventDelegate _winEventProc;

    /// <summary>正在运行的进程名集合只读视图。</summary>
    public IReadOnlyCollection<string> RunningProcesses => _visibleProcesses;

    /// <summary>当运行进程集合发生变化时触发。</summary>
    public event Action<string, bool>? RunningStateChanged;

    /// <summary>当 explorer.exe 重启（任务栏重建）时触发。</summary>
    public event Action? TaskbarRecreated;

    public WindowMonitor()
    {
        _winEventProc = WinEventCallback;
        HookEvents();
        ScanExistingWindows();
    }

    /// <summary>判断指定进程名当前是否有可见顶层窗口。</summary>
    public bool IsProcessRunning(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName))
            return false;

        return _visibleProcesses.Contains(Path.GetFileNameWithoutExtension(exeName));
    }

    /// <summary>刷新全部状态：重新枚举当前可见窗口。</summary>
    public void Refresh()
    {
        lock (_visibleProcesses)
        {
            _visibleProcesses.Clear();
            ScanExistingWindows();
        }
    }

    private void HookEvents()
    {
        uint[] events =
        {
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_OBJECT_SHOW,
            NativeMethods.EVENT_OBJECT_DESTROY,
            NativeMethods.EVENT_SYSTEM_MINIMIZEEND,
            NativeMethods.EVENT_SYSTEM_MINIMIZESTART,
        };

        foreach (uint evt in events)
        {
            var hHook = NativeMethods.SetWinEventHook(evt, evt, IntPtr.Zero, _winEventProc,
                0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
            if (hHook == IntPtr.Zero)
                Logger.Warn("SetWinEventHook 失败: event=0x{0:X}", evt);
            else
                _hookHandles.Add(hHook);
        }
    }

    private void ScanExistingWindows()
    {
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (ShouldIgnore(hWnd))
                return true;

            if (NativeMethods.IsWindowVisible(hWnd) && NativeMethods.GetParent(hWnd) == IntPtr.Zero)
            {
                RegisterWindow(hWnd);
            }
            return true;
        }, IntPtr.Zero);
    }

    private void WinEventCallback(
        IntPtr hWinEventHook, uint eventType, IntPtr hWnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (hWnd == IntPtr.Zero || idObject != 0)
            return;

        try
        {
            switch (eventType)
            {
                case NativeMethods.EVENT_SYSTEM_FOREGROUND:
                    if (ShouldIgnore(hWnd))
                        break;
                    RegisterWindow(hWnd);
                    break;

                case NativeMethods.EVENT_OBJECT_SHOW:
                    if (ShouldIgnore(hWnd))
                        break;
                    if (NativeMethods.IsWindowVisible(hWnd) && NativeMethods.GetParent(hWnd) == IntPtr.Zero)
                        RegisterWindow(hWnd);
                    break;

                case NativeMethods.EVENT_OBJECT_DESTROY:
                case NativeMethods.EVENT_SYSTEM_MINIMIZESTART:
                    if (ShouldIgnore(hWnd))
                        break;
                    UnregisterWindow(hWnd);
                    break;

                case NativeMethods.EVENT_SYSTEM_MINIMIZEEND:
                    if (ShouldIgnore(hWnd))
                        break;
                    if (NativeMethods.IsWindowVisible(hWnd))
                        RegisterWindow(hWnd);
                    break;
            }
        }
        catch { }
    }

    /// <summary>判断窗口是否应被忽略（系统窗口/工具窗口/无标题）。</summary>
    private static bool ShouldIgnore(IntPtr hWnd)
    {
        try
        {
            if (!NativeMethods.IsWindowVisible(hWnd))
                return true;

            var exStyle = NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWL_EXSTYLE);
            if ((exStyle.ToInt64() & 0x00000080L) != 0)
                return true;

            if (NativeMethods.GetParent(hWnd) != IntPtr.Zero)
                return true;

            var sb = new StringBuilder(256);
            NativeMethods.GetClassName(hWnd, sb, sb.Capacity);
            var className = sb.ToString();
            if (className.Contains("Toolbar", StringComparison.OrdinalIgnoreCase)
                || className.Contains("Notification", StringComparison.OrdinalIgnoreCase)
                || className.Contains("Ghost", StringComparison.OrdinalIgnoreCase))
                return true;

            var rect = new RECT();
            if (NativeMethods.GetWindowRect(hWnd, out rect))
            {
                int w = rect.right - rect.left;
                int h = rect.bottom - rect.top;
                if (w < 20 || h < 20)
                    return true;
            }

            return false;
        }
        catch
        {
            return true;
        }
    }

    private void RegisterWindow(IntPtr hWnd)
    {
        NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
        if (pid == 0)
            return;

        try
        {
            using var proc = Process.GetProcessById((int)pid);
            var name = proc.ProcessName;
            if (ExcludedProcesses.Contains(name))
                return;

            var exeName = Path.GetFileNameWithoutExtension(proc.MainModule?.FileName ?? name);
            if (string.IsNullOrWhiteSpace(exeName))
                return;

            lock (_visibleProcesses)
            {
                var added = _visibleProcesses.Add(exeName);
                if (added)
                    OnRunningStateChanged(exeName, true);
            }
        }
        catch { }
    }

    private void UnregisterWindow(IntPtr hWnd)
    {
        NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
        if (pid == 0)
            return;

        try
        {
            using var proc = Process.GetProcessById((int)pid);
            var exeName = Path.GetFileNameWithoutExtension(proc.MainModule?.FileName ?? proc.ProcessName);

            // 确认该 PID 没有其他可见顶层窗口
            bool hasOtherVisible = false;
            NativeMethods.EnumWindows((h, _) =>
            {
                if (h == hWnd)
                    return true;
                uint pid2;
                NativeMethods.GetWindowThreadProcessId(h, out pid2);
                if (pid2 == pid && !ShouldIgnore(h) && NativeMethods.IsWindowVisible(h))
                {
                    hasOtherVisible = true;
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            if (!hasOtherVisible)
            {
                lock (_visibleProcesses)
                {
                    if (_visibleProcesses.Remove(exeName))
                        OnRunningStateChanged(exeName, false);
                }
            }
        }
        catch { }
    }

    private void OnRunningStateChanged(string exeName, bool isRunning)
    {
        RunningStateChanged?.Invoke(exeName, isRunning);
        if (string.Equals(exeName, "explorer", StringComparison.OrdinalIgnoreCase))
            TaskbarRecreated?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var hHook in _hookHandles)
            NativeMethods.UnhookWinEvent(hHook);

        _hookHandles.Clear();
        _visibleProcesses.Clear();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    ~WindowMonitor() => Dispose();

    private bool _disposed;
}
