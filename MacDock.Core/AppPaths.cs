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

    /// <summary>Dock 项目持久化文件路径。</summary>
    public static string DockItemsFile => Path.Combine(AppDataDirectory, "dock-items.json");

    /// <summary>确保数据目录存在并返回其路径。</summary>
    public static string EnsureDataDirectory()
    {
        var dir = AppDataDirectory;
        Directory.CreateDirectory(dir);
        return dir;
    }
}
