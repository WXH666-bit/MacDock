using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MacDock.Core.Interop;
using NLog;

namespace MacDock.Core.Services;

/// <summary>
/// 窗口监控服务：通过 SetWinEventHook 监听系统窗口事件（前台切换/显示/销毁），
/// 维护当前具有可见顶层窗口的进程集合，供 Dock 显示运行状态小圆点。
/// 语义与 macOS 一致：最小化不等于退出，最小化的应用仍视为运行中。
/// </summary>
public sealed class WindowMonitor : IDisposable
{
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>保护 <see cref="_visibleProcesses"/> 与 <see cref="_pidToExeName"/> 的锁对象。</summary>
    private readonly object _sync = new();

    /// <summary>有可见顶层窗口的进程名集合（不含扩展名，忽略大小写）。</summary>
    private readonly HashSet<string> _visibleProcesses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>PID → 进程名映射，用于进程已退出时仍能定位要注销的进程名。</summary>
    private readonly Dictionary<uint, string> _pidToExeName = new();

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

    /// <summary>正在运行的进程名快照。</summary>
    public IReadOnlyCollection<string> RunningProcesses
    {
        get
        {
            lock (_sync)
                return _visibleProcesses.ToArray();
        }
    }

    /// <summary>当运行进程集合发生变化时触发（参数：进程名、是否运行中）。</summary>
    public event Action<string, bool>? RunningStateChanged;

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

        lock (_sync)
            return _visibleProcesses.Contains(Path.GetFileNameWithoutExtension(exeName));
    }

    /// <summary>刷新全部状态：重新枚举当前可见窗口。</summary>
    public void Refresh()
    {
        lock (_sync)
        {
            _visibleProcesses.Clear();
            _pidToExeName.Clear();
        }

        ScanExistingWindows();
    }

    private void HookEvents()
    {
        uint[] events =
        {
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_OBJECT_SHOW,
            NativeMethods.EVENT_OBJECT_DESTROY,
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
                    // 销毁时窗口已不可见，不能走 ShouldIgnore（它以可见性为前提）
                    UnregisterWindow(hWnd);
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

        var exeName = ResolveExeName(pid);
        if (exeName is null || ExcludedProcesses.Contains(exeName))
            return;

        bool added;
        lock (_sync)
        {
            _pidToExeName[pid] = exeName;
            added = _visibleProcesses.Add(exeName);
        }

        if (added)
            RunningStateChanged?.Invoke(exeName, true);
    }

    private void UnregisterWindow(IntPtr hWnd)
    {
        NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
        if (pid == 0)
            return;

        // 进程可能已退出，此时 Process.GetProcessById 会抛异常；回落到 PID → 进程名缓存
        string? exeName = ResolveExeName(pid);
        if (exeName is null)
        {
            lock (_sync)
                _pidToExeName.TryGetValue(pid, out exeName);
        }

        if (string.IsNullOrWhiteSpace(exeName) || ExcludedProcesses.Contains(exeName))
            return;

        // 确认该 PID 没有其他可见顶层窗口（多窗口应用关掉一个窗口时圆点应保留）
        bool hasOtherVisible = false;
        NativeMethods.EnumWindows((h, _) =>
        {
            if (h == hWnd)
                return true;
            NativeMethods.GetWindowThreadProcessId(h, out uint pid2);
            if (pid2 == pid && !ShouldIgnore(h))
            {
                hasOtherVisible = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        if (hasOtherVisible)
            return;

        bool removed;
        lock (_sync)
        {
            _pidToExeName.Remove(pid);

            // 同名进程可能有多个实例（如多开的 exe），仍有其他 PID 时不熄灯
            var stillAlive = _pidToExeName.Values.Any(
                v => string.Equals(v, exeName, StringComparison.OrdinalIgnoreCase));
            removed = !stillAlive && _visibleProcesses.Remove(exeName);
        }

        if (removed)
            RunningStateChanged?.Invoke(exeName, false);
    }

    /// <summary>取 PID 对应的进程名（不含扩展名）；进程已退出或无权访问时返回 null。</summary>
    private static string? ResolveExeName(uint pid)
    {
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            var name = proc.ProcessName;
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (ArgumentException)
        {
            // 进程已退出
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var hHook in _hookHandles)
            NativeMethods.UnhookWinEvent(hHook);

        _hookHandles.Clear();

        lock (_sync)
        {
            _visibleProcesses.Clear();
            _pidToExeName.Clear();
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }

    ~WindowMonitor() => Dispose();

    private bool _disposed;
}
