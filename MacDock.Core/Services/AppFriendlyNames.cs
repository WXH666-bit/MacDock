namespace MacDock.Core.Services;

/// <summary>
/// 前台应用的进程名 → 中文友好名映射。菜单栏用其把「notepad」等英文进程名
/// 换成「记事本」，UWP 包显示名解析留给 M2.3。
/// 匹配大小写不敏感，未命中返回 null（由调用方回退到窗口标题）。
/// </summary>
public static class AppFriendlyNames
{
    /// <summary>内部进程（dwm 等），菜单栏直接忽略不上报。</summary>
    public static readonly IReadOnlyCollection<string> IgnoredProcesses = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "dwm",
    };

    private static readonly IReadOnlyDictionary<string, string> FriendlyNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["notepad"] = "记事本",
            ["explorer"] = "文件资源管理器",
            ["msedge"] = "Microsoft Edge",
            ["chrome"] = "Google Chrome",
            ["firefox"] = "Firefox",
            ["WINWORD"] = "Microsoft Word",
            ["EXCEL"] = "Microsoft Excel",
            ["POWERPNT"] = "PowerPoint",
            ["WINWORD.EXE"] = "Microsoft Word",
            ["EXCEL.EXE"] = "Microsoft Excel",
            ["POWERPNT.EXE"] = "PowerPoint",
            ["WeChat"] = "微信",
            ["Weixin"] = "微信",
            ["QQ"] = "QQ",
            ["steam"] = "Steam",
            ["cloudmusic"] = "网易云音乐",
            ["QQMusic"] = "QQ音乐",
            ["PotPlayerMini64"] = "PotPlayer",
            ["Code"] = "Visual Studio Code",
            ["idea64"] = "IntelliJ IDEA",
            ["devenv"] = "Visual Studio",
            ["Snipaste"] = "Snipaste",
            ["mstsc"] = "远程桌面连接",
            ["Pinyin"] = "微软拼音",
            ["WPS"] = "WPS Office",
            ["wps"] = "WPS Office",
        };

    /// <summary>
    /// 取进程名对应的中文友好名。未命中返回 null。
    /// 支持带扩展名（"notepad.exe"）与纯名（"notepad"）两种写法的匹配。
    /// </summary>
    public static string? TryGetFriendlyName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return null;

        // 去掉路径与扩展名，得到纯进程名（如 "notepad.exe" → "notepad"）
        var bareName = Path.GetFileNameWithoutExtension(processName.Trim());
        if (string.IsNullOrWhiteSpace(bareName))
            return null;

        return FriendlyNames.TryGetValue(bareName, out var friendly) ? friendly : null;
    }

    /// <summary>判断进程名是否应被菜单栏忽略（内部系统进程）。</summary>
    public static bool IsIgnored(string? processName)
        => !string.IsNullOrWhiteSpace(processName)
            && IgnoredProcesses.Contains(Path.GetFileNameWithoutExtension(processName));
}
