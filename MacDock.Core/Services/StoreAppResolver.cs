using System.Diagnostics;
using System.Security.Principal;
using Windows.Management.Deployment;

namespace MacDock.Core.Services;

/// <summary>
/// 商店应用（MSIX 打包应用）解析与启动：按可执行名匹配当前用户已安装包的 AUMID，
/// 通过 shell:AppsFolder\AUMID 拉起。用于 Win11 上 calc.exe 等系统组件商店化的场景。
/// </summary>
public static class StoreAppResolver
{
    // 常见商店应用可执行名 → 包名（已知映射优先，避免模糊匹配误中同名包）
    private static readonly Dictionary<string, string> KnownPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["calc"] = "Microsoft.WindowsCalculator",
        ["mspaint"] = "Microsoft.Paint",
        ["notepad"] = "Microsoft.WindowsNotepad",
    };

    /// <summary>按可执行名（不含扩展名）解析 AUMID；未找到返回 null。</summary>
    public static string? ResolveAumid(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName))
            return null;

        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            if (sid is null)
                return null;

            var packageManager = new PackageManager();
            var packages = packageManager.FindPackagesForUser(sid);

            // 已知映射优先
            if (KnownPackages.TryGetValue(exeName, out var packageName))
            {
                var known = packages.FirstOrDefault(p =>
                    string.Equals(p.Id.Name, packageName, StringComparison.OrdinalIgnoreCase));
                var aumid = GetFirstAumid(known);
                if (aumid is not null)
                    return aumid;
            }

            // 模糊匹配：包名包含可执行名
            foreach (var package in packages)
            {
                if (!package.Id.Name.Contains(exeName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var aumid = GetFirstAumid(package);
                if (aumid is not null)
                    return aumid;
            }
        }
        catch
        {
            // WinRT 枚举失败（权限、裁剪环境等）按未找到处理
        }

        return null;
    }

    /// <summary>通过 shell:AppsFolder\AUMID 启动商店应用。</summary>
    public static void LaunchByAumid(string aumid)
    {
        Process.Start(new ProcessStartInfo($"shell:AppsFolder\\{aumid}") { UseShellExecute = true });
    }

    private static string? GetFirstAumid(Package? package)
    {
        if (package is null)
            return null;

        try
        {
            var entries = package.GetAppListEntriesAsync().GetAwaiter().GetResult();
            return entries.FirstOrDefault()?.AppUserModelId;
        }
        catch
        {
            return null;
        }
    }
}
