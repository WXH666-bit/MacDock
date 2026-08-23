using System.Runtime.InteropServices;
using System.Text;
using MacDock.Core.Interop;
using MacDock.Core.Models;
using NLog;

namespace MacDock.Core.Services;

/// <summary>
/// 托盘图标读取器抽象（便于 UI 层注入假实现做差量/转发逻辑测试）。
/// </summary>
public interface ITrayIconReader : IDisposable
{
    /// <summary>全量读取可见托盘 + 溢出区的图标（含 IsOverflow 标记），失败时该链为空。</summary>
    IReadOnlyList<TrayIconInfo> Read();

    /// <summary>廉价探测可见托盘按钮数（仅 TB_BUTTONCOUNT，微秒级），供 500ms 轮询节流。</summary>
    uint ProbeVisibleCount();

    /// <summary>廉价探测溢出区按钮数。</summary>
    uint ProbeOverflowCount();
}

/// <summary>
/// 托盘图标读取器：跨进程只读读取 explorer 任务栏通知区的按钮数据。
/// 两条窗口链：可见托盘 Shell_TrayWnd→TrayNotifyWnd→SysPager→ToolbarWindow32，
/// 溢出区 NotifyIconOverflowWindow→SysPager→ToolbarWindow32。
/// 从 TBBUTTON.dwData（explorer 进程内指针）读 24 字节托盘项数据，得到 hWnd/uID/回调/图标。
///
/// 全程按 x64 假设；任何一步失败该链返回空列表（不抛异常），只 Warn 一次。
/// 只读，不做任何对 explorer 的写入。
/// </summary>
public sealed class TrayIconReader : ITrayIconReader
{
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// explorer 重启广播消息 ID（TaskbarCreated）。订阅方加入 WndProc 监听，
    /// 收到后重置托盘区并重枚举（explorer 重启后托盘窗口全部重建）。
    /// </summary>
    public static uint TaskbarCreatedMessage { get; } = LazyTaskbarCreated();

    private readonly ITrayToolbarScan _scan;
    private bool _disposed;

    public TrayIconReader() : this(new Win32TrayToolbarScan())
    {
    }

    private static uint LazyTaskbarCreated()
    {
        var message = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
        return message;
    }

    /// <summary>供单测注入假扫描器。</summary>
    internal TrayIconReader(ITrayToolbarScan scan)
    {
        _scan = scan ?? throw new ArgumentNullException(nameof(scan));
    }

    /// <inheritdoc />
    public IReadOnlyList<TrayIconInfo> Read()
    {
        ThrowIfDisposed();

        try
        {
            var result = new List<TrayIconInfo>();
            AppendChain(result, overflow: false);
            AppendChain(result, overflow: true);
            return result;
        }
        catch (Exception exception)
        {
            Logger.Warn(exception, "读取托盘图标失败，返回空集合");
            return Array.Empty<TrayIconInfo>();
        }
    }

    /// <inheritdoc />
    public uint ProbeVisibleCount()
    {
        ThrowIfDisposed();
        return _scan.CountChain(overflow: false);
    }

    /// <inheritdoc />
    public uint ProbeOverflowCount()
    {
        ThrowIfDisposed();
        return _scan.CountChain(overflow: true);
    }

    public void Dispose()
    {
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void AppendChain(List<TrayIconInfo> target, bool overflow)
    {
        var raw = _scan.ScanChain(overflow);
        if (raw is null)
            return;

        foreach (var item in raw)
        {
            if (item.HwndTarget == IntPtr.Zero)
                continue;

            target.Add(new TrayIconInfo(
                Key: TrayIconInfo.BuildKey(item.HwndTarget, item.UId),
                HIcon: item.HIcon,
                Tooltip: item.Tooltip,
                IsOverflow: overflow,
                HwndTarget: item.HwndTarget,
                UCallbackMessage: item.CallbackMessage,
                UId: item.UId));
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TrayIconReader));
    }
}

/// <summary>跨进程扫描到的单个托盘按钮原始数据。</summary>
internal sealed record RawTrayButton(
    int Index,
    IntPtr HwndTarget,
    uint UId,
    uint CallbackMessage,
    IntPtr HIcon,
    string? Tooltip);

/// <summary>托盘工具栏扫描抽象（真实实现走 P/Invoke 跨进程内存读取）。</summary>
internal interface ITrayToolbarScan
{
    /// <summary>扫描一条链的工具栏按钮；该链任何失败返回空集合。</summary>
    IReadOnlyList<RawTrayButton> ScanChain(bool overflow);

    /// <summary>廉价探测一条链的按钮数量。</summary>
    uint CountChain(bool overflow);
}

/// <summary>
/// Win32 扫描实现：查找工具栏 → OpenProcess(explorer) → 远程缓冲读 TBBUTTON → 读托盘项数据。
/// 全程只读；用毕 VirtualFreeEx 释放远程缓冲。
/// </summary>
internal sealed class Win32TrayToolbarScan : ITrayToolbarScan
{
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>TBBUTTON.dwData 指向的托盘项数据前 24 字节。</summary>
    private const int TrayItemDataSize = 24;

    public IReadOnlyList<RawTrayButton> ScanChain(bool overflow)
    {
        var hwndToolbar = FindToolbar(overflow);
        if (hwndToolbar == IntPtr.Zero)
        {
            LogChainWarn(overflow, "未找到托盘工具栏窗口");
            return Array.Empty<RawTrayButton>();
        }

        NativeMethods.GetWindowThreadProcessId(hwndToolbar, out uint pid);
        if (pid == 0)
            return Array.Empty<RawTrayButton>();

        var hProcess = TrayInterop.OpenProcess(
            TrayInterop.PROCESS_VM_OPERATION | TrayInterop.PROCESS_VM_READ | TrayInterop.PROCESS_VM_WRITE,
            bInheritHandle: false,
            pid);
        if (hProcess == IntPtr.Zero)
        {
            LogChainWarn(overflow, "OpenProcess(explorer) 失败");
            return Array.Empty<RawTrayButton>();
        }

        try
        {
            return ScanToolbar(hProcess, hwndToolbar, overflow);
        }
        catch (Exception exception)
        {
            LogChainWarn(overflow, $"扫描托盘工具栏异常：{exception.Message}");
            return Array.Empty<RawTrayButton>();
        }
        finally
        {
            TrayInterop.CloseHandle(hProcess);
        }
    }

    public uint CountChain(bool overflow)
    {
        var hwndToolbar = FindToolbar(overflow);
        if (hwndToolbar == IntPtr.Zero)
            return 0;

        var count = (int)TrayInterop.SendMessageW(hwndToolbar, TrayInterop.TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero);
        return count > 0 ? (uint)count : 0;
    }

    private static List<RawTrayButton> ScanToolbar(IntPtr hProcess, IntPtr hwndToolbar, bool overflow)
    {
        var count = (int)TrayInterop.SendMessageW(hwndToolbar, TrayInterop.TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero);
        if (count <= 0)
            return new List<RawTrayButton>();

        var result = new List<RawTrayButton>(count);
        int buttonSize = Marshal.SizeOf<TBBUTTON>();

        for (int i = 0; i < count; i++)
        {
            var rawButton = ReadButton(hProcess, hwndToolbar, i, buttonSize);
            if (rawButton is null)
                continue;

            var data = ReadTrayItemData(hProcess, rawButton.Value.dwData);
            if (data is null || data.Value.hWnd == IntPtr.Zero)
                continue;

            var tooltip = ReadTooltip(hProcess, hwndToolbar, i);
            result.Add(new RawTrayButton(
                Index: i,
                HwndTarget: data.Value.hWnd,
                UId: data.Value.uID,
                CallbackMessage: data.Value.uCallbackMessage,
                HIcon: data.Value.hIcon,
                Tooltip: tooltip));
        }

        return result;
    }

    /// <summary>TB_GETBUTTON 读取第 index 个 TBBUTTON；失败返回 null。</summary>
    private static TBBUTTON? ReadButton(IntPtr hProcess, IntPtr hwndToolbar, int index, int size)
    {
        var remote = TrayInterop.VirtualAllocEx(
            hProcess, IntPtr.Zero, (UIntPtr)size, TrayInterop.MEM_COMMIT, TrayInterop.PAGE_READWRITE);
        if (remote == IntPtr.Zero)
            return null;

        try
        {
            TrayInterop.SendMessageW(hwndToolbar, TrayInterop.TB_GETBUTTON, (IntPtr)index, remote);

            var local = Marshal.AllocHGlobal(size);
            try
            {
                if (!ReadRemote(hProcess, remote, local, size))
                    return null;

                return Marshal.PtrToStructure<TBBUTTON>(local);
            }
            finally
            {
                Marshal.FreeHGlobal(local);
            }
        }
        finally
        {
            TrayInterop.VirtualFreeEx(hProcess, remote, UIntPtr.Zero, TrayInterop.MEM_RELEASE);
        }
    }

    /// <summary>读 dwData 指向的托盘项数据（hWnd/uID/回调/hIcon 24 字节）。</summary>
    private static TrayInterop.TrayItemData? ReadTrayItemData(IntPtr hProcess, IntPtr dwData)
    {
        if (dwData == IntPtr.Zero)
            return null;

        var local = Marshal.AllocHGlobal(TrayItemDataSize);
        try
        {
            if (!ReadRemote(hProcess, dwData, local, TrayItemDataSize))
                return null;

            return Marshal.PtrToStructure<TrayInterop.TrayItemData>(local);
        }
        finally
        {
            Marshal.FreeHGlobal(local);
        }
    }

    /// <summary>TB_GETBUTTONTEXTW 读按钮 tooltip（跨进程缓冲），失败返回 null。</summary>
    private static string? ReadTooltip(IntPtr hProcess, IntPtr hwndToolbar, int index)
    {
        // 先问长度（lParam=0），返回按钮文本字符数；不可靠时兜底 128 字符
        int len = (int)TrayInterop.SendMessageW(hwndToolbar, TrayInterop.TB_GETBUTTONTEXTW, (IntPtr)index, IntPtr.Zero);
        if (len <= 0)
            len = 128;

        int chars = len + 1;
        int bytes = chars * sizeof(char); // WCHAR：2 字节
        var remote = TrayInterop.VirtualAllocEx(hProcess, IntPtr.Zero, (UIntPtr)bytes, TrayInterop.MEM_COMMIT, TrayInterop.PAGE_READWRITE);
        if (remote == IntPtr.Zero)
            return null;

        try
        {
            TrayInterop.SendMessageW(hwndToolbar, TrayInterop.TB_GETBUTTONTEXTW, (IntPtr)index, remote);

            var local = Marshal.AllocHGlobal(bytes);
            try
            {
                if (!ReadRemote(hProcess, remote, local, bytes))
                    return null;

                var text = Marshal.PtrToStringUni(local);
                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }
            finally
            {
                Marshal.FreeHGlobal(local);
            }
        }
        finally
        {
            TrayInterop.VirtualFreeEx(hProcess, remote, UIntPtr.Zero, TrayInterop.MEM_RELEASE);
        }
    }

    private static bool ReadRemote(IntPtr hProcess, IntPtr remote, IntPtr local, int size)
        => TrayInterop.ReadProcessMemory(
            hProcess, remote, local, (UIntPtr)size, out var bytesRead) && bytesRead == (UIntPtr)size;

    /// <summary>按链类型查找到 ToolbarWindow32 句柄。</summary>
    private static IntPtr FindToolbar(bool overflow)
    {
        if (overflow)
        {
            var top = TrayInterop.FindWindowW(TrayInterop.NotifyIconOverflowWindow, null);
            return FindToolbarUnder(top);
        }

        var shell = TrayInterop.FindWindowW(TrayInterop.ShellTrayWnd, null);
        var tray = shell == IntPtr.Zero
            ? IntPtr.Zero
            : TrayInterop.FindWindowExW(shell, IntPtr.Zero, TrayInterop.TrayNotifyWnd, null);
        return FindToolbarUnder(tray);
    }

    private static IntPtr FindToolbarUnder(IntPtr parent)
    {
        if (parent == IntPtr.Zero)
            return IntPtr.Zero;

        var pager = TrayInterop.FindWindowExW(parent, IntPtr.Zero, TrayInterop.SysPager, null);
        return pager == IntPtr.Zero
            ? IntPtr.Zero
            : TrayInterop.FindWindowExW(pager, IntPtr.Zero, TrayInterop.ToolbarWindow32, null);
    }

    private void LogChainWarn(bool overflow, string reason)
        => Logger.Warn("托盘图标读取：{0} 链失败（{1}），返回空集合", overflow ? "溢出区" : "可见托盘", reason);
}
