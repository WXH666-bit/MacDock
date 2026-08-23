namespace MacDock.Core.Models;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public bool HideWindowsTaskbar { get; set; }

    /// <summary>
    /// 菜单栏是否用 AppBar 保留顶部工作区（默认开）。
    /// AppBar 副作用不可控时用户可置 false 一键回退到覆盖式（M2.1 行为）。
    /// </summary>
    public bool MenuBarReserveWorkArea { get; set; } = true;

    /// <summary>
    /// 是否接管任务栏托盘（默认开）。跨进程只读 explorer 托盘数据属高风险特性，
    /// 出问题时可置 false 一键回退（菜单栏不显示托盘区，原生任务栏托盘不受影响）。
    /// </summary>
    public bool TrayTakeover { get; set; } = true;
}
