using System.Runtime.InteropServices;
using MacDock.Core.Interop;
using MacDock.Core.Models;
using Microsoft.Win32;

namespace MacDock.Core.Services;

/// <summary>
/// 机器信息读取（供「关于本机」使用）：CPU 型号与系统版本走注册表，
/// 内存总量走 GlobalMemoryStatusEx。任一项失败都以占位文本降级，不抛异常。
/// </summary>
public static class SystemInfoService
{
    private const string ProcessorKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string Unknown = "未知";

    /// <summary>读取机器信息。调用方应放到后台线程（注册表 + WMI 级开销）。</summary>
    public static SystemInfo Read() => new(
        ReadProcessorName(),
        ReadTotalMemoryGb(),
        ReadOperatingSystem(),
        ReadMachineName());

    /// <summary>CPU 型号：HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0 → ProcessorNameString。</summary>
    private static string ReadProcessorName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ProcessorKey);
            var name = key?.GetValue("ProcessorNameString") as string;
            return string.IsNullOrWhiteSpace(name) ? Unknown : name.Trim();
        }
        catch
        {
            return Unknown;
        }
    }

    /// <summary>物理内存总量（GB，一位小数）。读取失败返回 0。</summary>
    private static double ReadTotalMemoryGb()
    {
        try
        {
            var status = new NativeMethods.MEMORYSTATUSEX
            {
                dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>(),
            };

            if (!NativeMethods.GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0)
                return 0;

            return Math.Round(status.ullTotalPhys / (1024.0 * 1024.0 * 1024.0), 1);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>系统版本：ProductName + DisplayVersion（如「Windows 11 专业版 23H2」）。</summary>
    private static string ReadOperatingSystem()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(CurrentVersionKey);
            if (key is null)
                return Unknown;

            var product = key.GetValue("ProductName") as string;
            var display = key.GetValue("DisplayVersion") as string;
            var build = key.GetValue("CurrentBuild") as string;

            // Win11 的 ProductName 仍写着 Windows 10，按内部版本号纠正
            if (!string.IsNullOrWhiteSpace(product)
                && Environment.OSVersion.Version.Build >= 22000
                && product.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
            {
                product = product.Replace("Windows 10", "Windows 11", StringComparison.OrdinalIgnoreCase);
            }

            if (string.IsNullOrWhiteSpace(product))
                return Unknown;

            var text = product.Trim();
            if (!string.IsNullOrWhiteSpace(display))
                text += $" {display.Trim()}";
            if (!string.IsNullOrWhiteSpace(build))
                text += $"（内部版本 {build.Trim()}）";

            return text;
        }
        catch
        {
            return Unknown;
        }
    }

    private static string ReadMachineName()
    {
        try
        {
            var name = Environment.MachineName;
            return string.IsNullOrWhiteSpace(name) ? Unknown : name;
        }
        catch
        {
            return Unknown;
        }
    }
}
