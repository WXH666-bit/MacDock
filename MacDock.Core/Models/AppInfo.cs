namespace MacDock.Core.Models;

/// <summary>
/// 运行中的应用 / 顶层窗口信息（M3 窗口监听与运行指示使用，M1 先占位）。
/// </summary>
public sealed class AppInfo
{
    /// <summary>窗口句柄。</summary>
    public IntPtr Hwnd { get; set; }

    /// <summary>窗口标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>进程可执行文件路径。</summary>
    public string? ProcessPath { get; set; }

    /// <summary>进程 ID。</summary>
    public int ProcessId { get; set; }

    /// <summary>窗口是否可见。</summary>
    public bool IsVisible { get; set; }
}
