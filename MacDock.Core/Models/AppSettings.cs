namespace MacDock.Core.Models;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 2;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public bool HideWindowsTaskbar { get; set; }

    /// <summary>
    /// 菜单栏是否用 AppBar 保留顶部工作区（安全默认关闭）。
    /// 该功能会直接参与 Windows Shell 工作区协调，必须由用户显式开启。
    /// </summary>
    public bool MenuBarReserveWorkArea { get; set; }

    /// <summary>
    /// 是否接管任务栏托盘（安全默认关闭）。该功能依赖 explorer 内部实现，
    /// 必须由用户显式开启；关闭时不显示菜单栏托盘区，原生任务栏托盘不受影响。
    /// </summary>
    public bool TrayTakeover { get; set; }
}
