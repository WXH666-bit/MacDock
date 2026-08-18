using System.Runtime.InteropServices;

namespace MacDock.Core.Services;

/// <summary>
/// 快捷方式解析：通过 WScript.Shell COM 解析 .lnk 的真实目标与图标位置。
/// </summary>
public static class ShortcutResolver
{
    /// <summary>是否为快捷方式文件。</summary>
    public static bool IsShortcut(string path) =>
        string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase);

    /// <summary>解析 .lnk 返回真实目标、图标路径与参数；非 .lnk 直接返回原路径。</summary>
    public static ShortcutInfo Resolve(string path)
    {
        if (!IsShortcut(path) || !File.Exists(path))
            return new ShortcutInfo(path, path, null);

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
                return new ShortcutInfo(path, path, null);

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(path);
            string target = (string)shortcut.TargetPath ?? path;
            string iconLocation = (string)shortcut.IconLocation ?? target;
            string? arguments = shortcut.Arguments as string;

            Marshal.ReleaseComObject(shortcut);
            Marshal.ReleaseComObject(shell);

            // IconLocation 形如 "path,index"；提取纯路径部分
            var iconPath = iconLocation.Contains(',')
                ? iconLocation[..iconLocation.IndexOf(',')]
                : iconLocation;

            return new ShortcutInfo(
                target,
                string.IsNullOrWhiteSpace(iconPath) ? target : iconPath,
                arguments);
        }
        catch
        {
            return new ShortcutInfo(path, path, null);
        }
    }
}

/// <summary>快捷方式解析结果。</summary>
public sealed record ShortcutInfo(string TargetPath, string IconPath, string? Arguments);
