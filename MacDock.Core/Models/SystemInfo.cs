namespace MacDock.Core.Models;

/// <summary>
/// 「关于本机」展示的机器信息快照。
/// </summary>
/// <param name="ProcessorName">CPU 型号（注册表 ProcessorNameString）。</param>
/// <param name="TotalMemoryGb">物理内存总量（GB，已四舍五入）。</param>
/// <param name="OperatingSystem">系统版本（ProductName + DisplayVersion）。</param>
/// <param name="MachineName">主机名。</param>
public sealed record SystemInfo(
    string ProcessorName,
    double TotalMemoryGb,
    string OperatingSystem,
    string MachineName);
