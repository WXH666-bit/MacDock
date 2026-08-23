using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using MacDock.Core.Interop;

namespace MacDock.Core.Services;

/// <summary>
/// UWP（MSIX 打包）应用的本地化显示名解析：由 AUMID → shell 项 → 常规显示名。
/// 例如「Microsoft.WindowsCalculator_8wekyb3d8bbwe!App」→「计算器」。
/// 进程内缓存（AUMID→显示名，exe→AUMID）；解析失败返回 null，上层回落窗口标题。
/// 注意：ms-resource: 间接串解析失败也按 null 处理。
/// </summary>
public static class UwpDisplayNameResolver
{
    private static readonly ConcurrentDictionary<string, string?> DisplayNameCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string?> ExeToAumidCache = new(StringComparer.Ordinal);

    /// <summary>按可执行名（不含扩展名）解析 AUMID（带缓存，避免每次前台切换重新枚举 WinRT 包）。</summary>
    public static string? ResolveAumid(string? exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName))
            return null;

        var bareName = Path.GetFileNameWithoutExtension(exeName).Trim();
        if (bareName.Length == 0)
            return null;

        if (ExeToAumidCache.TryGetValue(bareName, out var cached))
            return cached;

        var aumid = StoreAppResolver.ResolveAumid(bareName);
        ExeToAumidCache[bareName] = aumid;
        return aumid;
    }

    /// <summary>取 AUMID 的本地化显示名；失败或返回 ms-resource 间接串时返回 null。</summary>
    public static string? GetDisplayName(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid))
            return null;

        if (DisplayNameCache.TryGetValue(aumid, out var cached))
            return cached;

        var name = TryGetFromShell(aumid);
        // ms-resource: 间接串解析不出真身，按失败处理（避免把资源 ID 当名字显示）
        if (name is null || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            name = null;

        DisplayNameCache[aumid] = name;
        return name;
    }

    private static string? TryGetFromShell(string aumid)
    {
        try
        {
            var iid = typeof(IShellItem).GUID;
            var hr = NativeMethods.SHCreateItemFromParsingName(
                aumid, IntPtr.Zero, ref iid, out var item);
            if (hr != 0 || item is null)
                return null;

            try
            {
                var nameHr = item.GetDisplayName(
                    NativeMethods.SIGDN_NORMALDISPLAY, out string? name);
                return nameHr == 0 ? name : null;
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
        catch
        {
            return null;
        }
    }
}
