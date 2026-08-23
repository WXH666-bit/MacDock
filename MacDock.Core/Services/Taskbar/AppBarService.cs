using System.Runtime.InteropServices;
using MacDock.Core.Interop;
using NLog;

namespace MacDock.Core.Services.Taskbar;

/// <summary>AppBar Shell API 的最小抽象（便于单测注入假实现）。</summary>
internal interface IAppBarShell
{
    /// <summary>注册或取回全局唯一窗口消息 ID（如 "MacDock.AppBarNotify"）。失败返回 0。</summary>
    uint RegisterMessage(string messageName);

    /// <summary>调用 SHAppBarMessage。返回值含义随 dwMessage 变化（ABM_SETPOS 成功返回非 0）。</summary>
    IntPtr SendMessage(uint message, ref NativeMethods.APPBARDATA data);

    /// <summary>取主显示器完整边界（物理像素）。</summary>
    bool GetPrimaryMonitorBounds(out RECT bounds);
}

/// <summary>
/// AppBar 注册服务：用 SHAppBarMessage 让系统为菜单栏保留主屏顶部 32px 工作区，
/// 最大化窗口不再被菜单栏压住（macOS 菜单栏的正解做法）。
///
/// 生命周期：Register（ABM_NEW + QUERYPOS/SETPOS）→ DPI/分辨率变化重新 SETPOS →
/// 全屏 ABN_FULLSCREENAPP 让位（隐藏窗口但不注销）→ Unregister（ABM_REMOVE）。
///
/// 注册失败时降级为覆盖式（M2.1 行为），只 Warn 一次，绝不抛异常。
/// </summary>
public sealed class AppBarService : IDisposable
{
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>AppBar 回调消息名（RegisterWindowMessageW 转成全局唯一 ID）。</summary>
    public const string CallbackMessageName = "MacDock.AppBarNotify";

    private readonly IAppBarShell _shell;
    private readonly object _sync = new();

    private IntPtr _hwnd;
    private uint _callbackMessage;
    private bool _registered;
    private bool _degradedLogged;

    /// <summary>系统分配的回调消息 ID（0 = 尚未注册）。MenuBarWindow 用它过滤 WndProc 消息。</summary>
    public uint CallbackMessage
    {
        get
        {
            lock (_sync)
                return _callbackMessage;
        }
    }

    /// <summary>当前是否已成功注册（未注册 = 覆盖式降级）。</summary>
    public bool IsRegistered
    {
        get
        {
            lock (_sync)
                return _registered;
        }
    }

    public AppBarService() : this(new Win32AppBarShell())
    {
    }

    internal AppBarService(IAppBarShell shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
    }

    /// <summary>
    /// 注册 AppBar 并申请主屏顶部指定高度的工作区。
    /// 幂等：重复调用会先重新 SETPOS（用于 DPI/分辨率变化后校准），不会重复 ABM_NEW。
    /// </summary>
    /// <param name="hwnd">菜单栏窗口句柄（回调消息发到这里）。</param>
    /// <param name="heightPx">通栏高度（物理像素）。</param>
    /// <returns>是否注册成功（失败 = 保持覆盖式降级）。</returns>
    public bool Register(IntPtr hwnd, int heightPx)
    {
        if (hwnd == IntPtr.Zero || heightPx <= 0)
            return false;

        lock (_sync)
        {
            try
            {
                if (_callbackMessage == 0)
                {
                    var message = _shell.RegisterMessage(CallbackMessageName);
                    if (message == 0)
                    {
                        LogDegradedOnce("RegisterWindowMessageW(\"" + CallbackMessageName + "\") 失败");
                        return false;
                    }

                    _callbackMessage = message;
                }

                if (!_registered)
                {
                    _hwnd = hwnd;
                    var data = BuildData(_hwnd, _callbackMessage);
                    var result = _shell.SendMessage(NativeMethods.ABM_NEW, ref data);
                    if (result == IntPtr.Zero)
                    {
                        LogDegradedOnce("ABM_NEW 注册失败");
                        return false;
                    }

                    _registered = true;
                }

                // SETPOS 失败时回滚 ABM_NEW，避免出现「已注册但不占工作区」的中间态
                if (!ApplyPosition(heightPx))
                {
                    var data = BuildData(_hwnd, _callbackMessage);
                    try
                    {
                        _shell.SendMessage(NativeMethods.ABM_REMOVE, ref data);
                    }
                    catch
                    {
                        // 回滚失败只能放弃：进程退出时仍会走一次 ABM_REMOVE 兜底
                    }

                    _registered = false;
                    _hwnd = IntPtr.Zero;
                    return false;
                }

                return true;
            }
            catch (Exception exception)
            {
                LogDegradedOnce($"AppBar 注册异常：{exception.Message}");
                return false;
            }
        }
    }

    /// <summary>重新申请工作区（DPI / 分辨率 / 缩放变化后调用）。</summary>
    public bool UpdatePosition(int heightPx)
    {
        if (heightPx <= 0)
            return false;

        lock (_sync)
        {
            if (!_registered)
                return false;

            try
            {
                return ApplyPosition(heightPx);
            }
            catch (Exception exception)
            {
                Logger.Warn(exception, "AppBar 重新申请工作区失败");
                return false;
            }
        }
    }

    /// <summary>
    /// 处理回调消息（MenuBarWindow 的 WndProc 收到 AppBar 消息后转交这里）。
    /// 只处理 ABN_FULLSCREENAPP：全屏应用出现时通知 UI 隐藏，退出时通知恢复。
    /// </summary>
    /// <returns>是否为全屏状态切换事件（true = 进入全屏，false = 退出全屏，null = 非全屏事件）。</returns>
    public bool? HandleCallback(IntPtr wParam, IntPtr lParam)
    {
        lock (_sync)
        {
            if (!_registered)
                return null;

            var notification = (uint)wParam.ToInt64();
            if (notification != NativeMethods.ABN_FULLSCREENAPP)
                return null;

            // lParam/uState 非 0 = 全屏应用出现；0 = 退出全屏
            return lParam != IntPtr.Zero;
        }
    }

    /// <summary>注销 AppBar 并归还工作区（ABM_REMOVE）。幂等，不抛异常。</summary>
    public void Unregister()
    {
        lock (_sync)
        {
            if (!_registered)
                return;

            try
            {
                var data = BuildData(_hwnd, _callbackMessage);
                _shell.SendMessage(NativeMethods.ABM_REMOVE, ref data);
            }
            catch (Exception exception)
            {
                Logger.Warn(exception, "AppBar 注销失败");
            }
            finally
            {
                _registered = false;
                _hwnd = IntPtr.Zero;
            }
        }
    }

    /// <summary>QUERYPOS + SETPOS 两段式申请：先问系统建议位置，再正式申请。</summary>
    private bool ApplyPosition(int heightPx)
    {
        // 主屏物理边界作为基准（AppBar 坐标是物理像素）
        var monitor = _shell.GetPrimaryMonitorBounds(out var bounds);
        if (!monitor)
            return false;

        var data = BuildData(_hwnd, _callbackMessage);
        data.uEdge = NativeMethods.ABE_TOP;
        data.rc = new RECT
        {
            left = bounds.left,
            top = bounds.top,
            right = bounds.right,
            bottom = bounds.top + heightPx,
        };

        // QUERYPOS：系统会按 AppBar 规则调整 rc（如与其他 AppBar 避让）
        _shell.SendMessage(NativeMethods.ABM_QUERYPOS, ref data);

        // SETPOS：正式申请。失败保持覆盖式（工作区不保留但不影响菜单栏显示）
        var result = _shell.SendMessage(NativeMethods.ABM_SETPOS, ref data);
        if (result == IntPtr.Zero)
        {
            LogDegradedOnce("ABM_SETPOS 申请工作区失败");
            return false;
        }

        return true;
    }

    private static NativeMethods.APPBARDATA BuildData(IntPtr hwnd, uint callbackMessage)
        => new()
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.APPBARDATA>(),
            hWnd = hwnd,
            uCallbackMessage = callbackMessage,
        };

    private void LogDegradedOnce(string reason)
    {
        if (_degradedLogged)
            return;

        _degradedLogged = true;
        Logger.Warn("AppBar 注册降级为覆盖式（{0}），菜单栏仍可用但最大化窗口顶部可能被压", reason);
    }

    public void Dispose() => Unregister();

    /// <summary>Win32 实现：直接调用 Shell API。</summary>
    private sealed class Win32AppBarShell : IAppBarShell
    {
        public uint RegisterMessage(string messageName)
            => NativeMethods.RegisterWindowMessageW(messageName);

        public IntPtr SendMessage(uint message, ref NativeMethods.APPBARDATA data)
            => NativeMethods.SHAppBarMessage(message, ref data);

        public bool GetPrimaryMonitorBounds(out RECT bounds)
        {
            bounds = default;
            var monitor = NativeMethods.MonitorFromPoint(
                new POINT { x = 0, y = 0 },
                NativeMethods.MONITOR_DEFAULTTOPRIMARY);
            if (monitor == IntPtr.Zero)
                return false;

            var info = new NativeMethods.MONITORINFO
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>(),
            };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info))
                return false;

            bounds = info.rcMonitor;
            return bounds.right > bounds.left;
        }
    }
}
