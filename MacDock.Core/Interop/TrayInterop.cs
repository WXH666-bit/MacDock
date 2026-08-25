using System.Runtime.InteropServices;

namespace MacDock.Core.Interop;

/// <summary>
/// 托盘（通知区）互操作声明集中地。用于读取 explorer 任务栏托盘按钮数据与转发点击消息。
/// 旧版工具栏读取会在 explorer 中分配临时输出缓冲，但不会修改其既有数据结构。
///
/// 全程按 x64 假设（MacDock 面向 64 位 Windows；TBBUTTON 结构在 x64 下为自然对齐 32 字节）。
/// </summary>
internal static class TrayInterop
{
    // ---- 窗口查找 ----
    /// <summary>Shell 任务栏顶层窗口类。</summary>
    public const string ShellTrayWnd = "Shell_TrayWnd";
    /// <summary>通知区容器类。</summary>
    public const string TrayNotifyWnd = "TrayNotifyWnd";
    /// <summary>溢出通知区的顶层窗口类。</summary>
    public const string NotifyIconOverflowWindow = "NotifyIconOverflowWindow";
    /// <summary>托盘/溢出区内部的分页容器类。</summary>
    public const string SysPager = "SysPager";
    /// <summary>实际承载按钮的工具栏类。</summary>
    public const string ToolbarWindow32 = "ToolbarWindow32";
    /// <summary>Windows 11 XAML 任务栏宿主；出现它且没有 ToolbarWindow32 时旧托盘结构不可用。</summary>
    public const string ModernTaskbarCoreWindow = "Windows.UI.Core.CoreWindow";

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern IntPtr FindWindowExW(
        IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    // ---- 窗口消息（ToolBar） ----
    /// <summary>WM_USER。</summary>
    public const uint WM_USER = 0x0400;
    /// <summary>TB_BUTTONCOUNT = WM_USER + 24：取按钮数量。</summary>
    public const uint TB_BUTTONCOUNT = WM_USER + 24;
    /// <summary>TB_GETBUTTON = WM_USER + 23：按索引取按钮（lParam 指向远程 TBBUTTON 缓冲）。</summary>
    public const uint TB_GETBUTTON = WM_USER + 23;
    // ---- 鼠标消息（点击转发） ----
    /// <summary>WM_LBUTTONUP：左键抬起。</summary>
    public const uint WM_LBUTTONUP = 0x0202;
    /// <summary>WM_RBUTTONUP：右键抬起。</summary>
    public const uint WM_RBUTTONUP = 0x0205;
    /// <summary>WM_LBUTTONDBLCLK：左键双击。</summary>
    public const uint WM_LBUTTONDBLCLK = 0x0203;

    // ---- 进程内存访问权限 ----
    public const int PROCESS_VM_OPERATION = 0x0008;
    public const int PROCESS_VM_READ = 0x0010;

    // ---- VirtualAllocEx 常量 ----
    public const uint MEM_COMMIT = 0x1000;
    public const uint MEM_RESERVE = 0x2000;
    public const uint MEM_RELEASE = 0x8000;
    public const uint PAGE_READWRITE = 0x04;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualAllocEx(
        IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, uint dwFreeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadProcessMemory(
        IntPtr hProcess, IntPtr lpBaseAddress, IntPtr lpBuffer, UIntPtr nSize, out UIntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    // ---- 窗口消息发送 ----
    /// <summary>
    /// SMTO_ABORTIFHUNG：若目标线程未在泵消息（explorer 假死）则立即中止等待，配合超时防卡调用线程。
    /// </summary>
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>
    /// SendMessageTimeoutW：带超时的同步发送窗口消息。目标窗口无响应（explorer 假死）时按
    /// fuFlags 中止等待并返回 false；调用方必须在后台线程调用，避免阻塞 UI。
    /// 返回值是 bool（失败可由 GetLastError 扩展），消息结果写入 lpdwResult。
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SendMessageTimeoutW(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint timeout, out IntPtr result);

    /// <summary>
    /// PostMessageW：异步投递窗口消息（不进队列等待）。用于向托盘目标窗口转发点击。
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ---- 托盘项数据（跨进程指针） ----
    /// <summary>
    /// 托盘项链表指针 dwData 指向的 explorer 内部数据，x64 下前 24 字节：
    /// hWnd(8) + uID(4) + uCallbackMessage(4) + hIcon(8)。这是点击转发的全部依据。
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct TrayItemData
    {
        [FieldOffset(0)] public IntPtr hWnd;
        [FieldOffset(8)] public uint uID;
        [FieldOffset(12)] public uint uCallbackMessage;
        [FieldOffset(16)] public IntPtr hIcon;
    }
}

/// <summary>
/// TBBUTTON 结构（x64，大小 32 字节）。自然对齐：iBitmap/idCommand 各 4 字节，
/// fsState/fsStyle 各 1 字节，其后 6 字节对齐填充使 dwData（8 字节指针）落在偏移 16，
/// iString 落在偏移 24。用显式布局锁定偏移，避免打包器按顺序排列造成错位。
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 32)]
internal struct TBBUTTON
{
    [FieldOffset(0)] public int iBitmap;
    [FieldOffset(4)] public int idCommand;
    [FieldOffset(8)] public byte fsState;
    [FieldOffset(9)] public byte fsStyle;
    // 偏移 10-15 为结构保留的 6 字节，不声明，留给对齐填充
    [FieldOffset(16)] public IntPtr dwData;
    [FieldOffset(24)] public IntPtr iString;
}
