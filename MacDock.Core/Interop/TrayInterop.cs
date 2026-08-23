using System.Runtime.InteropServices;

namespace MacDock.Core.Interop;

/// <summary>
/// 托盘（通知区）互操作声明集中地。仅用于只读读取 explorer 任务栏托盘的按钮数据与
/// 向托盘窗口转发点击消息；不做任何对 explorer 的写入操作。
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
    /// <summary>TB_GETBUTTONTEXTW = WM_USER + 75：取按钮文本（宽字符版）。</summary>
    public const uint TB_GETBUTTONTEXTW = WM_USER + 75;

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
    public const int PROCESS_VM_WRITE = 0x0020;

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
    public static extern bool WriteProcessMemory(
        IntPtr hProcess, IntPtr lpBaseAddress, IntPtr lpBuffer, UIntPtr nSize, out UIntPtr lpNumberOfBytesWritten);

    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr hObject);

    // ---- 窗口消息发送 ----
    /// <summary>SendMessageW：同步发送窗口消息（跨进程时由系统做缓冲区封送）。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

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
