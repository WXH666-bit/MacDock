using System.Runtime.InteropServices;
using MacDock.Core.Interop;
using MacDock.Core.Models;
using NLog;

namespace MacDock.Core.Services;

/// <summary>
/// 托盘图标读取器抽象（便于 UI 层注入假实现做差量/转发逻辑测试）。
/// </summary>
public interface ITrayIconReader : IDisposable
{
    /// <summary>
    /// 全量读取可见托盘 + 溢出区的图标（含 IsOverflow 标记）。
    /// 工具栏存在但没有按钮时 Items 为空；溢出弹层未创建由 OverflowAvailable=false 表达；
    /// 窗口、进程或消息读取失败时抛出 TrayIconReaderException。
    /// </summary>
    TrayIconReadResult Read();

    /// <summary>廉价探测可见托盘按钮数（仅 TB_BUTTONCOUNT），0 是合法空链；探测失败时抛出 TrayIconReaderException。</summary>
    uint ProbeVisibleCount();

    /// <summary>廉价探测溢出区按钮数；窗口尚未创建时返回 null，探测失败时抛出异常。</summary>
    uint? ProbeOverflowCount();
}

/// <summary>一次完整读取；溢出窗口不可用时 Items 仅含可见区，调用方应保留旧溢出集合。</summary>
public sealed record TrayIconReadResult(
    IReadOnlyList<TrayIconInfo> Items,
    bool OverflowAvailable);

/// <summary>
/// 托盘读取失败语义：与“工具栏存在但没有托盘图标”的合法空结果区分开。
/// 调用方应在后台线程捕获，并保留上一次成功集合等待下一次重试。
/// </summary>
public class TrayIconReaderException : InvalidOperationException
{
    public TrayIconReaderException(string message)
        : base(message)
    {
    }

    public TrayIconReaderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>当前 explorer 会话不宜继续探测；收到 TaskbarCreated 后可重新尝试。</summary>
public class TrayIconSessionUnavailableException : TrayIconReaderException
{
    public TrayIconSessionUnavailableException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// 当前 Shell 使用不公开托盘按钮结构（例如 Windows 11 XAML 任务栏），旧版 ToolbarWindow32
/// 读取路径永久不可用。调用方应停止轮询，等待 TaskbarCreated 后再重新探测。
/// </summary>
public sealed class TrayIconTopologyUnsupportedException : TrayIconSessionUnavailableException
{
    public TrayIconTopologyUnsupportedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// 带远程输出缓冲的窗口消息已超时。缓冲会留在 explorer 中，避免目标线程晚到后写入已释放内存；
/// 本次 explorer 会话停止继续枚举，因此一次故障最多遗留一个很小的临时区域。
/// </summary>
public sealed class TrayIconRemoteBufferAbandonedException : TrayIconSessionUnavailableException
{
    public TrayIconRemoteBufferAbandonedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// 托盘图标读取器：跨进程读取 explorer 任务栏通知区的按钮数据。
/// 两条窗口链：可见托盘 Shell_TrayWnd→TrayNotifyWnd→SysPager→ToolbarWindow32，
/// 溢出区 NotifyIconOverflowWindow→SysPager→ToolbarWindow32。
/// 从 TBBUTTON.dwData（explorer 进程内指针）读 24 字节托盘项数据，得到 hWnd/uID/回调/图标。
///
/// 全程按 x64 假设；工具栏不存在、跨进程读取失败或消息超时会抛出 TrayIconReaderException，
/// 以免把合法空托盘与读取失败混淆。
/// 仅让工具栏把结果写入临时远程缓冲，不修改 explorer 既有数据结构。
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
    private int _disposed;

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
    public TrayIconReadResult Read()
    {
        ThrowIfDisposed();

        try
        {
            var result = new List<TrayIconInfo>();
            var visible = _scan.ScanChain(overflow: false);
            if (!visible.Available)
                throw new TrayIconReaderException("可见托盘扫描意外返回不可用状态");

            AppendChain(result, visible.Items, overflow: false);
            var overflow = _scan.ScanChain(overflow: true);
            if (overflow.Available)
                AppendChain(result, overflow.Items, overflow: true);

            return new TrayIconReadResult(result, overflow.Available);
        }
        catch (Exception exception)
        {
            Logger.Warn(exception, "读取托盘图标失败");
            if (exception is TrayIconReaderException)
                throw;

            throw new TrayIconReaderException("读取托盘图标失败", exception);
        }
    }

    /// <inheritdoc />
    public uint ProbeVisibleCount()
    {
        ThrowIfDisposed();
        try
        {
            return _scan.CountChain(overflow: false)
                ?? throw new TrayIconReaderException("可见托盘计数意外返回不可用状态");
        }
        catch (Exception exception)
        {
            Logger.Warn(exception, "探测可见托盘按钮数失败");
            if (exception is TrayIconReaderException)
                throw;

            throw new TrayIconReaderException("探测可见托盘按钮数失败", exception);
        }
    }

    /// <inheritdoc />
    public uint? ProbeOverflowCount()
    {
        ThrowIfDisposed();
        try
        {
            return _scan.CountChain(overflow: true);
        }
        catch (Exception exception)
        {
            Logger.Warn(exception, "探测溢出托盘按钮数失败");
            if (exception is TrayIconReaderException)
                throw;

            throw new TrayIconReaderException("探测溢出托盘按钮数失败", exception);
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        GC.SuppressFinalize(this);
    }

    private static void AppendChain(
        List<TrayIconInfo> target,
        IReadOnlyList<RawTrayButton> raw,
        bool overflow)
    {
        foreach (var item in raw)
        {
            if (item.HwndTarget == IntPtr.Zero)
            {
                throw new TrayIconReaderException(
                    $"{(overflow ? "溢出区" : "可见托盘")}第 {item.Index} 个按钮缺少目标窗口");
            }

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
        if (Volatile.Read(ref _disposed) != 0)
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

/// <summary>一条工具栏链的扫描结果；不可用与合法空集合必须分开表达。</summary>
internal sealed record TrayToolbarScanResult(
    bool Available,
    IReadOnlyList<RawTrayButton> Items);

/// <summary>托盘工具栏扫描抽象（真实实现走 P/Invoke 跨进程内存读取）。</summary>
internal interface ITrayToolbarScan
{
    /// <summary>扫描一条链的工具栏按钮；合法空链返回空集合，扫描失败时抛出异常。</summary>
    TrayToolbarScanResult ScanChain(bool overflow);

    /// <summary>廉价探测一条链的按钮数量。</summary>
    uint? CountChain(bool overflow);
}

/// <summary>
/// Win32 扫描实现：查找工具栏 → OpenProcess(explorer) → 远程缓冲读 TBBUTTON → 读托盘项数据。
/// 不修改 explorer 已有数据；仅分配临时输出缓冲，并在消息确认返回后释放。
/// </summary>
internal sealed class Win32TrayToolbarScan : ITrayToolbarScan
{
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>TBBUTTON.dwData 指向的托盘项数据前 24 字节。</summary>
    private const int TrayItemDataSize = 24;

    /// <summary>SendMessageTimeoutW 超时（毫秒）：explorer 假死时快速放弃等待。</summary>
    private const uint MessageTimeoutMs = 50;

    /// <summary>
    /// NOTIFYICONDATA.szTip 的公开上限（含结尾 NUL）。只读这么多字符，避免依赖
    /// 没有目标缓冲区长度参数的 TB_GETBUTTONTEXTW。
    /// </summary>
    private const int MaxTooltipCharacters = 128;

    public TrayToolbarScanResult ScanChain(bool overflow)
    {
        EnsureSupportedArchitecture(overflow);

        var hwndToolbar = FindToolbar(overflow);
        if (hwndToolbar == IntPtr.Zero)
        {
            // Windows 可能在没有打开溢出面板时尚未创建 NotifyIconOverflowWindow；
            // 这表示合法的空溢出区，不应阻塞可见托盘读取或触发退避。
            if (overflow)
                return new TrayToolbarScanResult(false, Array.Empty<RawTrayButton>());

            throw MissingVisibleToolbarFailure();
        }

        NativeMethods.GetWindowThreadProcessId(hwndToolbar, out uint pid);
        if (pid == 0)
            throw ChainFailure(overflow, "无法取得托盘工具栏所属进程");

        var hProcess = TrayInterop.OpenProcess(
            TrayInterop.PROCESS_VM_OPERATION | TrayInterop.PROCESS_VM_READ,
            bInheritHandle: false,
            pid);
        if (hProcess == IntPtr.Zero)
            throw ChainFailure(overflow, "OpenProcess(explorer) 失败");

        try
        {
            return new TrayToolbarScanResult(
                true,
                ScanToolbar(hProcess, hwndToolbar, overflow));
        }
        catch (TrayIconReaderException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw ChainFailure(overflow, $"扫描托盘工具栏异常：{exception.Message}", exception);
        }
        finally
        {
            if (!TrayInterop.CloseHandle(hProcess))
            {
                Logger.Warn(
                    "关闭 explorer 托盘扫描句柄失败（Win32={0}）",
                    Marshal.GetLastWin32Error());
            }
        }
    }

    public uint? CountChain(bool overflow)
    {
        EnsureSupportedArchitecture(overflow);

        var hwndToolbar = FindToolbar(overflow);
        if (hwndToolbar == IntPtr.Zero)
        {
            // 溢出弹层按需创建；null 表示本轮不可观察，不能据此清空旧集合。
            if (overflow)
                return null;

            throw MissingVisibleToolbarFailure();
        }

        // 带超时：explorer 假死时快速放弃；0 仅表示工具栏存在但没有按钮。
        if (!TrayInterop.SendMessageTimeoutW(
            hwndToolbar, TrayInterop.TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero,
            TrayInterop.SMTO_ABORTIFHUNG, MessageTimeoutMs, out var countResult))
            throw ChainFailure(overflow, "TB_BUTTONCOUNT 超时或失败");

        var count = (int)countResult;
        if (count < 0)
            throw ChainFailure(overflow, "TB_BUTTONCOUNT 返回失败码");

        return (uint)count;
    }

    private static List<RawTrayButton> ScanToolbar(IntPtr hProcess, IntPtr hwndToolbar, bool overflow)
    {
        // 带超时：explorer 假死时放弃读取，并把失败交给上层退避重试。
        if (!TrayInterop.SendMessageTimeoutW(
            hwndToolbar, TrayInterop.TB_BUTTONCOUNT, IntPtr.Zero, IntPtr.Zero,
            TrayInterop.SMTO_ABORTIFHUNG, MessageTimeoutMs, out var countResult))
            throw new TrayIconReaderException($"{(overflow ? "溢出区" : "可见托盘")} TB_BUTTONCOUNT 超时或失败");

        var count = (int)countResult;
        if (count < 0)
            throw new TrayIconReaderException($"{(overflow ? "溢出区" : "可见托盘")} TB_BUTTONCOUNT 返回失败码");

        if (count == 0)
            return new List<RawTrayButton>();

        var result = new List<RawTrayButton>(count);
        int buttonSize = Marshal.SizeOf<TBBUTTON>();

        for (int i = 0; i < count; i++)
        {
            var rawButton = ReadButton(hProcess, hwndToolbar, i, buttonSize);
            var data = ReadTrayItemData(hProcess, rawButton.dwData);
            if (data.hWnd == IntPtr.Zero)
                throw new TrayIconReaderException($"第 {i} 个托盘按钮缺少目标窗口");

            var tooltip = ReadTooltip(hProcess, rawButton.iString);
            result.Add(new RawTrayButton(
                Index: i,
                HwndTarget: data.hWnd,
                UId: data.uID,
                CallbackMessage: data.uCallbackMessage,
                HIcon: data.hIcon,
                Tooltip: tooltip));
        }

        return result;
    }

    /// <summary>TB_GETBUTTON 读取第 index 个 TBBUTTON；失败即放弃整个快照，保留 UI 上一帧。</summary>
    private static TBBUTTON ReadButton(IntPtr hProcess, IntPtr hwndToolbar, int index, int size)
    {
        var remote = TrayInterop.VirtualAllocEx(
            hProcess,
            IntPtr.Zero,
            (UIntPtr)size,
            TrayInterop.MEM_COMMIT | TrayInterop.MEM_RESERVE,
            TrayInterop.PAGE_READWRITE);
        if (remote == IntPtr.Zero)
            throw new TrayIconReaderException(
                $"为第 {index} 个托盘按钮分配远程缓冲失败（Win32={Marshal.GetLastWin32Error()}）");

        var releaseRemote = true;
        try
        {
            // 超时不能证明目标窗口过程已经停止使用 lParam。此时保留这块 32 字节远程缓冲，
            // 并停掉当前 explorer 会话的后续枚举，避免“晚到写入已释放地址”。
            if (!TrayInterop.SendMessageTimeoutW(
                hwndToolbar, TrayInterop.TB_GETBUTTON, (IntPtr)index, remote,
                TrayInterop.SMTO_ABORTIFHUNG, MessageTimeoutMs, out var buttonResult))
            {
                releaseRemote = false;
                throw new TrayIconRemoteBufferAbandonedException(
                    $"第 {index} 个托盘按钮 TB_GETBUTTON 超时；已保留远程缓冲并停止本会话枚举");
            }

            if (buttonResult == IntPtr.Zero)
                throw new TrayIconReaderException($"第 {index} 个托盘按钮 TB_GETBUTTON 返回 FALSE");

            var local = Marshal.AllocHGlobal(size);
            try
            {
                if (!ReadRemote(hProcess, remote, local, size))
                    throw new TrayIconReaderException(
                        $"读取第 {index} 个托盘按钮数据失败（Win32={Marshal.GetLastWin32Error()}）");

                return Marshal.PtrToStructure<TBBUTTON>(local);
            }
            finally
            {
                Marshal.FreeHGlobal(local);
            }
        }
        finally
        {
            if (releaseRemote)
                ReleaseRemoteOrThrow(hProcess, remote, $"第 {index} 个托盘按钮");
        }
    }

    /// <summary>读 dwData 指向的托盘项数据（hWnd/uID/回调/hIcon 24 字节）。</summary>
    private static TrayInterop.TrayItemData ReadTrayItemData(IntPtr hProcess, IntPtr dwData)
    {
        if (dwData == IntPtr.Zero)
            throw new TrayIconReaderException("托盘按钮 dwData 为空，快照不完整");

        var local = Marshal.AllocHGlobal(TrayItemDataSize);
        try
        {
            if (!ReadRemote(hProcess, dwData, local, TrayItemDataSize))
                throw new TrayIconReaderException(
                    $"读取托盘项数据失败（Win32={Marshal.GetLastWin32Error()}）");

            return Marshal.PtrToStructure<TrayInterop.TrayItemData>(local);
        }
        finally
        {
            Marshal.FreeHGlobal(local);
        }
    }

    /// <summary>
    /// 从 TBBUTTON.iString 指向的 explorer 内存只读 tooltip。iString 也可能是字符串池索引；
    /// 这种情况不发送无边界的 TB_GETBUTTONTEXTW，而是安全降级为无 tooltip。
    /// </summary>
    private static string? ReadTooltip(IntPtr hProcess, IntPtr remoteString)
    {
        var address = remoteString.ToInt64();
        // IS_INTRESOURCE：高位为 0 时这是字符串池索引，不是可解引用的远程地址。
        if (address <= ushort.MaxValue)
            return null;

        const int chars = MaxTooltipCharacters;
        const int bytes = chars * sizeof(char);
        var local = Marshal.AllocHGlobal(bytes);
        try
        {
            // tooltip 是可选元数据；读取失败不能把完整的图标快照误判为空或失败。
            if (!ReadRemote(hProcess, remoteString, local, bytes))
                return null;

            var raw = Marshal.PtrToStringUni(local, chars);
            var terminator = raw?.IndexOf('\0') ?? -1;
            if (terminator < 0)
                return null;

            var text = raw![..terminator].Trim();
            return text.Length == 0 ? null : text;
        }
        finally
        {
            Marshal.FreeHGlobal(local);
        }
    }

    private static bool ReadRemote(IntPtr hProcess, IntPtr remote, IntPtr local, int size)
        => TrayInterop.ReadProcessMemory(
            hProcess, remote, local, (UIntPtr)size, out var bytesRead) && bytesRead == (UIntPtr)size;

    private static void ReleaseRemoteOrThrow(IntPtr hProcess, IntPtr remote, string context)
    {
        if (!TrayInterop.VirtualFreeEx(hProcess, remote, UIntPtr.Zero, TrayInterop.MEM_RELEASE))
        {
            throw new TrayIconSessionUnavailableException(
                $"释放 {context} 远程缓冲失败（Win32={Marshal.GetLastWin32Error()}），已停止本会话枚举");
        }
    }

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
        var toolbar = pager == IntPtr.Zero
            ? IntPtr.Zero
            : TrayInterop.FindWindowExW(pager, IntPtr.Zero, TrayInterop.ToolbarWindow32, null);

        // 某些旧版 Shell 没有 SysPager 中间层，兼容直接挂在通知区下的工具栏。
        return toolbar != IntPtr.Zero
            ? toolbar
            : TrayInterop.FindWindowExW(parent, IntPtr.Zero, TrayInterop.ToolbarWindow32, null);
    }

    private static TrayIconReaderException ChainFailure(bool overflow, string reason, Exception? inner = null)
    {
        var message = $"托盘图标读取：{(overflow ? "溢出区" : "可见托盘")}链失败（{reason}）";
        return inner is null
            ? new TrayIconReaderException(message)
            : new TrayIconReaderException(message, inner);
    }

    private static void EnsureSupportedArchitecture(bool overflow)
    {
        if (!Environment.Is64BitProcess)
            throw ChainFailure(overflow, "当前进程不是 x64，无法安全解析 explorer 的 64 位托盘结构");
    }

    private static TrayIconReaderException MissingVisibleToolbarFailure()
    {
        var shell = TrayInterop.FindWindowW(TrayInterop.ShellTrayWnd, null);
        var modernTaskbar = shell != IntPtr.Zero
            && TrayInterop.FindWindowExW(
                shell,
                IntPtr.Zero,
                TrayInterop.ModernTaskbarCoreWindow,
                null) != IntPtr.Zero;

        if (modernTaskbar)
        {
            return new TrayIconTopologyUnsupportedException(
                "当前 Windows 使用 XAML 通知区且未暴露 ToolbarWindow32；已停止不受支持的托盘枚举");
        }

        return ChainFailure(overflow: false, "未找到托盘工具栏窗口");
    }
}
