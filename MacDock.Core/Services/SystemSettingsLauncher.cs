using System.Diagnostics;

namespace MacDock.Core.Services;

/// <summary>控制中心允许打开的 Windows 设置页。</summary>
public enum SystemSettingsPage
{
    Wifi,
    Bluetooth,
    FocusAssist,
}

/// <summary>
/// 通过受限白名单打开 Windows 设置。这里不直接切换无线、蓝牙或通知状态，
/// 避免静默改变系统配置。
/// </summary>
public static class SystemSettingsLauncher
{
    /// <summary>返回设置页对应的 Windows 官方 ms-settings URI。</summary>
    public static Uri GetUri(SystemSettingsPage page)
        => page switch
        {
            SystemSettingsPage.Wifi => new Uri("ms-settings:network-wifi"),
            SystemSettingsPage.Bluetooth => new Uri("ms-settings:bluetooth"),
            SystemSettingsPage.FocusAssist => new Uri("ms-settings:quiethours"),
            _ => throw new ArgumentOutOfRangeException(nameof(page)),
        };

    /// <summary>使用 Windows 协议处理器打开指定设置页。</summary>
    public static void Open(SystemSettingsPage page)
    {
        using var process = Process.Start(new ProcessStartInfo(GetUri(page).AbsoluteUri)
        {
            UseShellExecute = true,
        });
    }
}
