using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using MacDock.Core.Interop;

namespace MacDock.Core.Services.Taskbar;

public sealed class Win32TaskbarPlatform : ITaskbarPlatform
{
    public nint FindWindow(string className)
        => NativeMethods.FindWindow(className, null);

    public bool IsWindow(nint handle)
        => NativeMethods.IsWindow(handle);

    public string? GetWindowClassName(nint handle)
    {
        var className = new StringBuilder(256);
        return NativeMethods.GetClassName(handle, className, className.Capacity) > 0
            ? className.ToString()
            : null;
    }

    public uint GetWindowProcessId(nint handle)
    {
        NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        return processId;
    }

    public string? GetProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException
            or OverflowException)
        {
            return null;
        }
    }

    public long? GetProcessStartTimeUtcTicks(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is ArgumentException
            or InvalidOperationException
            or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException
            or OverflowException)
        {
            return null;
        }
    }

    public nint GetWindowMonitor(nint handle)
        => NativeMethods.MonitorFromWindow(handle, NativeMethods.MONITOR_DEFAULTTONULL);

    public nint GetPrimaryMonitor()
    {
        var origin = new POINT { x = 0, y = 0 };
        return NativeMethods.MonitorFromPoint(origin, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
    }

    public bool IsWindowVisible(nint handle)
        => NativeMethods.IsWindowVisible(handle);

    public int? GetWindowShowCommand(nint handle)
    {
        var placement = new NativeMethods.WINDOWPLACEMENT
        {
            length = (uint)Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>(),
        };

        return NativeMethods.GetWindowPlacement(handle, ref placement)
            ? placement.showCmd
            : null;
    }

    public bool SetWindowShowState(nint handle, int command)
        => NativeMethods.ShowWindow(handle, command);
}
