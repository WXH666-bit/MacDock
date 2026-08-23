using System.Runtime.InteropServices;
using System.Reflection;
using MacDock.Core.Interop;
using Xunit;

namespace MacDock.Tests;

public sealed class NativeAbiTests
{
    [Fact]
    public void WindowPlacement_HasExpectedLayout()
    {
        Assert.Equal(44, Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>());
        Assert.Equal(8, Marshal.OffsetOf<NativeMethods.WINDOWPLACEMENT>("showCmd").ToInt32());
    }

    [Fact]
    public void RetainedWin32BoolReturns_UseExplicitUnmanagedBool()
    {
        var methodNames = new[]
        {
            "SetWindowPos",
            "GetCursorPos",
            "ShowWindow",
            "SetForegroundWindow",
            "BringWindowToTop",
            "AttachThreadInput",
            "DestroyIcon",
            "EnumWindows",
            "IsWindow",
            "IsWindowVisible",
            "GetWindowRect",
            "GetWindowPlacement",
            "UnhookWinEvent",
            "GetMonitorInfo",
            "GlobalMemoryStatusEx",
        };

        foreach (var methodName in methodNames)
        {
            var method = typeof(NativeMethods).GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(method);

            var marshalAs = method!.ReturnParameter.GetCustomAttribute<MarshalAsAttribute>();
            Assert.NotNull(marshalAs);
            Assert.Equal(UnmanagedType.Bool, marshalAs!.Value);
        }
    }

    [Fact]
    public void MemoryStatusEx_HasExpectedLayout()
    {
        // dwLength + dwMemoryLoad (2 * 4) + 7 个 ulong (7 * 8) = 64
        Assert.Equal(64, Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>());
        Assert.Equal(
            8,
            Marshal.OffsetOf<NativeMethods.MEMORYSTATUSEX>("ullTotalPhys").ToInt32());
    }

    [Fact]
    public void MonitorInfo_HasExpectedLayout()
    {
        // cbSize(4) + rcMonitor(16) + rcWork(16) + dwFlags(4) = 40
        Assert.Equal(40, Marshal.SizeOf<NativeMethods.MONITORINFO>());
        Assert.Equal(
            4,
            Marshal.OffsetOf<NativeMethods.MONITORINFO>("rcMonitor").ToInt32());
    }

    [Fact]
    public void UnicodeTextApis_UseUnicodeCharSet()
    {
        var methodNames = new[] { "GetWindowText", "GetMonitorInfo", "RegisterWindowMessageW" };

        foreach (var methodName in methodNames)
        {
            var method = typeof(NativeMethods).GetMethod(
                methodName,
                BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(method);

            var import = method!.GetCustomAttribute<DllImportAttribute>();
            Assert.NotNull(import);
            Assert.Equal(CharSet.Unicode, import!.CharSet);
        }
    }

    [Fact]
    public void RetainedWin32BoolParameters_UseExplicitUnmanagedBool()
    {
        var enumWindowsInvoke = typeof(NativeMethods.EnumWindowsProc).GetMethod("Invoke");
        Assert.NotNull(enumWindowsInvoke);
        var enumWindowsReturn = enumWindowsInvoke!.ReturnParameter.GetCustomAttribute<MarshalAsAttribute>();
        Assert.NotNull(enumWindowsReturn);
        Assert.Equal(UnmanagedType.Bool, enumWindowsReturn!.Value);

        var attachThreadInput = typeof(NativeMethods).GetMethod(
            "AttachThreadInput",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(attachThreadInput);
        var boolParameter = attachThreadInput!.GetParameters()
            .Single(parameter => parameter.ParameterType == typeof(bool));
        var marshalAs = boolParameter.GetCustomAttribute<MarshalAsAttribute>();
        Assert.NotNull(marshalAs);
        Assert.Equal(UnmanagedType.Bool, marshalAs!.Value);
    }
}
