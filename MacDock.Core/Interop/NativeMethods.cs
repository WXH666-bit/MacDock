using System.Runtime.InteropServices;
using System.Text;

namespace MacDock.Core.Interop;

/// <summary>
/// Win32 / Shell32 / DWM API 互操作声明集中地。
/// 架构分层约定：UI 层禁止直接声明 DllImport，统一经由此处与 Core 服务封装。
/// </summary>
internal static class NativeMethods
{
    // ---- 窗口扩展样式 ----
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_NOACTIVATE = 0x08000000L;
    public const long WS_EX_TOOLWINDOW = 0x00000080L;
    public const long WS_EX_TOPMOST = 0x00000008L;
    /// <summary>WS_EX_TRANSPARENT：鼠标点击穿透（用于纯视觉背景窗）。</summary>
    public const long WS_EX_TRANSPARENT = 0x00000020L;

    // ---- 窗口 Z 序 ----
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_NOZORDER = 0x0004;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    // ---- 光标 ----
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    // ---- DWM 属性（Win11） ----
    /// <summary>DWMWA_SYSTEMBACKDROP_TYPE（Win11 22H2+）。</summary>
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    /// <summary>DWMSBT_TRANSIENTWINDOW：亚克力。</summary>
    public const int DWMSBT_TRANSIENTWINDOW = 3;
    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE（Win11）。</summary>
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    /// <summary>DWMWCP_ROUND：圆角。</summary>
    public const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    /// <summary>查询桌面窗口管理器合成是否可用；M4 不可用时直接使用系统最小化。</summary>
    [DllImport("dwmapi.dll")]
    public static extern int DwmIsCompositionEnabled(
        [MarshalAs(UnmanagedType.Bool)] out bool pfEnabled);

    /// <summary>DWMWA_EXTENDED_FRAME_BOUNDS：窗口可见外框的物理像素边界。</summary>
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        out RECT value,
        int valueSize);

    // ---- 窗口合成属性（Accent 亚克力，Win10 1803+ / Win11 全系） ----
    /// <summary>WCA_ACCENT_POLICY。</summary>
    public const int WCA_ACCENT_POLICY = 19;
    /// <summary>ACCENT_ENABLE_ACRYLICBLURBEHIND：亚克力模糊 + 可控透明度底色。</summary>
    public const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    [DllImport("user32.dll")]
    public static extern int SetWindowCompositionAttribute(IntPtr hWnd, ref WindowCompositionAttributeData data);

    // ---- 窗口激活 ----
    public const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>挂靠/脱离两个线程的输入队列，用于绕过前台锁定策略。</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(
        uint idAttach,
        uint idAttachTo,
        [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    // ---- 窗口长值（跨 32/64 位兼容封装） ----
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern IntPtr GetWindowLong32(IntPtr hWnd, int nIndex);

    public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);

    public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
        => IntPtr.Size == 8 ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong) : SetWindowLong32(hWnd, nIndex, dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    // ---- Shell 图标 ----
    public const uint SHGFI_ICON = 0x000000100;
    public const uint SHGFI_LARGEICON = 0x000000000;
    public const uint SHGFI_SYSICONINDEX = 0x000004000;
    public const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

    public const int SHIL_LARGE = 0x0;
    public const int SHIL_SMALL = 0x1;
    public const int SHIL_EXTRALARGE = 0x2;
    public const int SHIL_JUMBO = 0x4;

    public const int ILD_TRANSPARENT = 0x00000001;

    /// <summary>IImageList COM 接口 IID。</summary>
    public static readonly Guid IID_IImageList = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");

    // ---- 精确主任务栏窗口 ----
    /// <summary>Shell_TrayWnd：Windows 主任务栏窗口类名。</summary>
    public const string SHELL_TRAY_WND = "Shell_TrayWnd";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    /// <summary>
    /// 注册（或取回）一个全局唯一的窗口消息 ID，用于接收 Shell 广播消息（如 TaskbarCreated）。
    /// 失败返回 0。
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    public static extern uint RegisterWindowMessageW(string lpString);

    // ---- 窗口枚举 ----
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [return: MarshalAs(UnmanagedType.Bool)]
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    /// <summary>取窗口标题。Unicode 版本，避免中文标题被 ANSI 转换损坏。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    // ---- 屏幕位图快照（M4 最小化动画） ----
    public const uint SRCCOPY = 0x00CC0020;
    public const uint CAPTUREBLT = 0x40000000;
    public const int COLORONCOLOR = 3;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int cx, int cy);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BitBlt(
        IntPtr hdc,
        int x,
        int y,
        int cx,
        int cy,
        IntPtr hdcSrc,
        int x1,
        int y1,
        uint rop);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool StretchBlt(
        IntPtr hdcDest,
        int xDest,
        int yDest,
        int widthDest,
        int heightDest,
        IntPtr hdcSrc,
        int xSrc,
        int ySrc,
        int widthSrc,
        int heightSrc,
        uint rop);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern int SetStretchBltMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr ho);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(IntPtr hdc);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    public const uint MONITOR_DEFAULTTONULL = 0x00000000;
    public const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    /// <summary>
    /// 取显示器信息（物理像素的完整边界与工作区）。
    /// 调用前必须把 cbSize 设为结构体大小。出处：user32.dll，winuser.h。
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    // ---- WinEventHook（窗口事件监听） ----
    /// <summary>WinEventHook 回调委托。</summary>
    public delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hWnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    /// <summary>挂钩前台窗口变化。</summary>
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    /// <summary>窗口从最小化状态恢复完成。</summary>
    public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    /// <summary>窗口开始最小化。</summary>
    public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    /// <summary>挂钩对象显示。</summary>
    public const uint EVENT_OBJECT_SHOW = 0x8002;
    /// <summary>挂钩对象销毁。</summary>
    public const uint EVENT_OBJECT_DESTROY = 0x8001;

    /// <summary>钩子来源范围：当前桌面所有进程。</summary>
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    // ---- SW_ 常量 ----
    public const int SW_HIDE = 0;
    public const int SW_SHOW = 5;

    // ---- AppBar（SHAppBarMessage，菜单栏工作区保留） ----
    /// <summary>ABM_NEW：注册一个新的 AppBar。</summary>
    public const uint ABM_NEW = 0x00000000;
    /// <summary>ABM_REMOVE：注销 AppBar 并归还工作区。</summary>
    public const uint ABM_REMOVE = 0x00000001;
    /// <summary>ABM_QUERYPOS：向系统询问建议位置（SETPOS 前先问）。</summary>
    public const uint ABM_QUERYPOS = 0x00000002;
    /// <summary>ABM_SETPOS：正式申请 AppBar 边界（系统可能调整后写回）。</summary>
    public const uint ABM_SETPOS = 0x00000003;
    /// <summary>ABN_FULLSCREENAPP：全屏应用出现/退出的通知（uState≠0 为进入全屏）。</summary>
    public const uint ABN_FULLSCREENAPP = 0x0000002;

    /// <summary>ABE_TOP：AppBar 紧贴屏幕上边缘。</summary>
    public const int ABE_TOP = 0;

    [DllImport("shell32.dll", SetLastError = true)]
    public static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    /// <summary>
    /// APPBARDATA：SHAppBarMessage 的输入输出结构（shellapi.h）。
    /// cbSize 必须在调用前赋值；rc 为建议/实际边界（物理像素）。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    // ---- 物理内存总量（关于本机） ----
    /// <summary>
    /// 取全局内存状态。调用前必须把 dwLength 设为结构体大小，否则失败。
    /// 出处：kernel32.dll，Windows SDK sysinfoapi.h。
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>MONITORINFO：显示器完整边界与工作区（物理像素）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    /// <summary>MEMORYSTATUSEX：GlobalMemoryStatusEx 的输出结构（sysinfoapi.h）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WINDOWPLACEMENT
    {
        public uint length;
        public uint flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll")]
    public static extern int SHGetImageList(int iImageList, ref Guid riid, out IntPtr ppv);

    // ---- Shell 项（UWP 应用显示名） ----
    /// <summary>SIGDN_NORMALDISPLAY：项的常规显示名（本地化）。</summary>
    public const uint SIGDN_NORMALDISPLAY = 0x00000000;

    /// <summary>
    /// 由解析名（如 AUMID "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App"）创建 shell 项。
    /// 返回 HRESULT；成功时经 iid 输出 IShellItem。
    /// </summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    public static extern int SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);
}

/// <summary>ACCENT_POLICY：窗口合成策略。GradientColor 格式 (A&lt;&lt;24)|(B&lt;&lt;16)|(G&lt;&lt;8)|R。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AccentPolicy
{
    public int AccentState;
    public int AccentFlags;
    public int GradientColor;
    public int AnimationId;
}

/// <summary>WINDOWCOMPOSITIONATTRIBUTEDATA。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WindowCompositionAttributeData
{
    public int Attribute;
    public IntPtr Data;
    public int SizeOfData;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int x;
    public int y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int left;
    public int top;
    public int right;
    public int bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IMAGELISTDRAWPARAMS
{
    public int cbSize;
    public IntPtr himl;
    public int i;
    public IntPtr hdcDst;
    public int x;
    public int y;
    public int cx;
    public int cy;
    public int xBitmap;
    public int yBitmap;
    public uint rgbBk;
    public uint rgbFg;
    public uint fStyle;
    public uint dwRop;
    public uint fState;
    public uint Frame;
    public uint crEffect;
}

/// <summary>
/// IImageList COM 接口最小声明。仅实际调用 GetIcon（第 8 个 vtable 槽），
/// 前面的方法按真实接口顺序声明以保证 vtable 布局正确。
/// </summary>
[ComImport]
[Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IImageList
{
    [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
    [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, ref int pi);
    [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
    [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
    [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, ref int pi);
    [PreserveSig] int Draw(ref IMAGELISTDRAWPARAMS pimldp);
    [PreserveSig] int Remove(int i);
    [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
}

/// <summary>
/// IShellItem：shell 项的最小声明。仅实际调用 GetDisplayName（第 3 个 vtable 槽），
/// 前面的方法按真实接口顺序声明以保证 vtable 布局正确。
/// </summary>
[ComImport]
[Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
    [PreserveSig] int GetParent(out IShellItem ppsi);
    [PreserveSig] int GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string? ppszName);
    [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
    [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
}
