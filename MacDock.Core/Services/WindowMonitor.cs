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

    /// <summary>WinEvent Hook 专用消息线程；安装、回调和注销都发生在该线程。</summary>
    private readonly WinEventHookWorker _hookWorker;

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

    /// <summary>
    /// 当前台应用切换时触发（参数：进程名、窗口标题）。
    /// 供菜单栏显示当前应用名。MacDock 自身与 explorer（桌面）不会上报，
    /// 由订阅方保持上一个应用名不变。
    /// </summary>
    public event Action<string, string?>? ForegroundAppChanged;

    /// <summary>
    /// 普通顶层窗口开始最小化时触发（窗口句柄、进程名）。订阅方只能做
    /// 可失败的视觉增强，不得延迟或取消系统原生最小化。
    /// </summary>
    public event Action<IntPtr, string>? WindowMinimizeStarted;

    /// <summary>MacDock 自身进程名，前台上报时排除。</summary>
    private static readonly string SelfProcessName = ResolveSelfProcessName();

    /// <summary>上一次上报的前台进程名 + 标题，用于去重（避免同一窗口反复上报）。</summary>
    private string? _lastForegroundKey;

    public WindowMonitor()
    {
        _winEventProc = WinEventCallback;
        _hookWorker = new WinEventHookWorker(
            HookEvents,
            UnhookEvents,
            exception => Logger.Error(exception, "WinEvent Hook 线程失败"));
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
        if (Volatile.Read(ref _disposeState) != 0)
            return;

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
            NativeMethods.EVENT_SYSTEM_MINIMIZESTART,
            NativeMethods.EVENT_OBJECT_SHOW,
            NativeMethods.EVENT_OBJECT_DESTROY,
        };

        foreach (uint evt in events)
        {
            var hHook = NativeMethods.SetWinEventHook(evt, evt, IntPtr.Zero, _winEventProc,
                0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
            if (hHook == IntPtr.Zero)
            {
                Logger.Warn(
                    "SetWinEventHook 失败: event=0x{0:X}, error={1}",
                    evt,
                    Marshal.GetLastWin32Error());
            }
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
        if (Volatile.Read(ref _disposeState) != 0
            || hWnd == IntPtr.Zero
            || idObject != 0)
        {
            return;
        }

        try
        {
            switch (eventType)
            {
                case NativeMethods.EVENT_SYSTEM_FOREGROUND:
                    if (ShouldIgnore(hWnd))
                        break;
                    RegisterWindow(hWnd);
                    ReportForegroundApp(hWnd);
                    break;

                case NativeMethods.EVENT_OBJECT_SHOW:
                    if (ShouldIgnore(hWnd))
                        break;
                    if (NativeMethods.IsWindowVisible(hWnd) && NativeMethods.GetParent(hWnd) == IntPtr.Zero)
                        RegisterWindow(hWnd);
                    break;

                case NativeMethods.EVENT_SYSTEM_MINIMIZESTART:
                    ReportMinimizeStarted(hWnd);
                    break;

                case NativeMethods.EVENT_OBJECT_DESTROY:
                    // 销毁时窗口已不可见，不能走 ShouldIgnore（它以可见性为前提）
                    UnregisterWindow(hWnd);
                    break;
            }
        }
        catch (Exception exception)
        {
            Logger.Debug(exception, "处理 WinEvent 回调失败: event=0x{0:X}", eventType);
        }
    }

    private void ReportMinimizeStarted(IntPtr hWnd)
    {
        if (ShouldIgnore(hWnd))
            return;

        NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
        if (pid == 0)
            return;

        var exeName = ResolveExeName(pid);
        if (string.IsNullOrWhiteSpace(exeName)
            || ExcludedProcesses.Contains(exeName)
            || string.Equals(exeName, SelfProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        WindowMinimizeStarted?.Invoke(hWnd, exeName);
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

    /// <summary>
    /// 上报前台应用（进程名 + 窗口标题）。排除 MacDock 自身与桌面：
    /// 前台切到桌面时不上报，订阅方保持上一个应用名不变。
    /// </summary>
    private void ReportForegroundApp(IntPtr hWnd)
    {
        var app = TryResolveForegroundApp(hWnd);
        if (app is null)
            return;

        var (exeName, title) = app.Value;

        // 同一窗口的重复上报（如反复点击同一应用）直接丢弃
        var key = $"{exeName}\0{title}";
        if (string.Equals(key, _lastForegroundKey, StringComparison.Ordinal))
            return;

        _lastForegroundKey = key;
        ForegroundAppChanged?.Invoke(exeName, title);
    }

    /// <summary>
    /// 取当前前台应用（进程名 + 窗口标题）；前台为桌面、MacDock 自身或无法确定时返回 null。
    /// 供菜单栏启动时取初值，避免等到第一次前台切换才有内容。
    /// </summary>
    public (string ProcessName, string? WindowTitle)? GetForegroundApp()
    {
        try
        {
            var hWnd = NativeMethods.GetForegroundWindow();
            return hWnd == IntPtr.Zero ? null : TryResolveForegroundApp(hWnd);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解析前台窗口归属的应用；应被忽略（桌面 / MacDock 自身）时返回 null。</summary>
    private static (string ProcessName, string? WindowTitle)? TryResolveForegroundApp(IntPtr hWnd)
    {
        NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
        if (pid == 0)
            return null;

        var exeName = ResolveExeName(pid);
        if (string.IsNullOrWhiteSpace(exeName))
            return null;

        if (string.Equals(exeName, SelfProcessName, StringComparison.OrdinalIgnoreCase))
            return null;

        // explorer 同时覆盖桌面与文件资源管理器：桌面（无标题）忽略，文件窗口（有标题）照报
        var title = GetWindowTitle(hWnd);
        if (string.Equals(exeName, "explorer", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(title))
            return null;

        return (exeName, title);
    }

    /// <summary>读取窗口标题；为空返回 null。</summary>
    private static string? GetWindowTitle(IntPtr hWnd)
    {
        var sb = new StringBuilder(512);
        var length = NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
        if (length <= 0)
            return null;

        var title = sb.ToString().Trim();
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }

    /// <summary>取当前进程名（不含扩展名），用于排除 MacDock 自身窗口。</summary>
    private static string ResolveSelfProcessName()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return self.ProcessName;
        }
        catch
        {
            return "MacDock";
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

    private void UnhookEvents()
    {
        foreach (var hHook in _hookHandles)
        {
            if (!NativeMethods.UnhookWinEvent(hHook))
            {
                Logger.Warn(
                    "UnhookWinEvent 失败: hook=0x{0:X}, error={1}",
                    hHook.ToInt64(),
                    Marshal.GetLastWin32Error());
            }
        }

        _hookHandles.Clear();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        _hookWorker.Dispose();

        lock (_sync)
        {
            _visibleProcesses.Clear();
            _pidToExeName.Clear();
        }

        GC.SuppressFinalize(this);
    }

    private int _disposeState;
}
