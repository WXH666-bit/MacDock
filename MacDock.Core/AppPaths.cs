namespace MacDock.Core;

/// <summary>
/// MacDock 应用数据路径集中定义。
/// </summary>
public static class AppPaths
{
    /// <summary>数据目录：%AppData%\MacDock。</summary>
    public static string AppDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MacDock");

    /// <summary>
    /// 应用数据根目录（%AppData% 本身）。Watchdog 参数校验以此为基准拼
    /// 「根\MacDock\taskbar-lease.json」，注意不是 <see cref="AppDataDirectory"/>——
    /// 传深一层会导致期望路径多出一级 MacDock，租约永远无法获取。
    /// </summary>
    public static string AppDataRoot => Environment.GetFolderPath(
        Environment.SpecialFolder.ApplicationData);

    /// <summary>Dock 项目持久化文件路径。</summary>
    public static string DockItemsFile => Path.Combine(AppDataDirectory, "dock-items.json");

    /// <summary>应用设置持久化文件路径。</summary>
    public static string SettingsFile => Path.Combine(AppDataDirectory, "settings.json");

    /// <summary>任务栏租约 journal 文件路径。</summary>
    public static string TaskbarLeaseFile => Path.Combine(AppDataDirectory, "taskbar-lease.json");

    /// <summary>任务栏租约独占锁文件路径。</summary>
    public static string TaskbarLeaseLockFile => Path.Combine(AppDataDirectory, "taskbar-lease.lock");

    /// <summary>确保数据目录存在并返回其路径。</summary>
    public static string EnsureDataDirectory()
    {
        var dir = AppDataDirectory;
        Directory.CreateDirectory(dir);
        return dir;
    }
}
