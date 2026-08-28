using System.Diagnostics;
using MacDock.Core.Models;

namespace MacDock.Core.Services;

/// <summary>
/// 把具有可见顶层窗口的进程解析为可安全显示、激活和固定的 Dock 项。
/// 进程模块和包目录访问可能较慢，调用方必须放在后台串行执行。
/// </summary>
public sealed class RunningDockItemResolver
{
    private static readonly HashSet<string> UnsupportedHostProcesses = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationFrameHost",
        "RuntimeBroker",
        "SearchHost",
        "ShellExperienceHost",
        "StartMenuExperienceHost",
        "TextInputHost",
    };

    private readonly Func<string, string?> _resolveExecutablePath;
    private readonly Func<string, string?> _resolveAumid;
    private readonly Func<string, string?> _resolveStoreDisplayName;
    private readonly Func<string, string?> _resolveFileDescription;

    /// <summary>创建使用当前 Windows 进程与 AppX 包信息的运行应用解析器。</summary>
    public RunningDockItemResolver()
        : this(
            TryResolveExecutablePath,
            UwpDisplayNameResolver.ResolveAumid,
            UwpDisplayNameResolver.GetDisplayName,
            TryResolveFileDescription)
    {
    }

    /// <summary>供单测注入不访问真实进程和包目录的解析器。</summary>
    internal RunningDockItemResolver(
        Func<string, string?> resolveExecutablePath,
        Func<string, string?> resolveAumid,
        Func<string, string?> resolveStoreDisplayName,
        Func<string, string?> resolveFileDescription)
    {
        _resolveExecutablePath = resolveExecutablePath
            ?? throw new ArgumentNullException(nameof(resolveExecutablePath));
        _resolveAumid = resolveAumid
            ?? throw new ArgumentNullException(nameof(resolveAumid));
        _resolveStoreDisplayName = resolveStoreDisplayName
            ?? throw new ArgumentNullException(nameof(resolveStoreDisplayName));
        _resolveFileDescription = resolveFileDescription
            ?? throw new ArgumentNullException(nameof(resolveFileDescription));
    }

    /// <summary>
    /// 解析一个运行进程。无法得到可靠的桌面启动路径或商店 AUMID 时返回 null，
    /// 避免产生只能显示、关闭后却无法再次启动的“坏死固定项”。
    /// </summary>
    public DockItem? Resolve(string? processName)
    {
        var bareName = NormalizeProcessName(processName);
        if (bareName is null || UnsupportedHostProcesses.Contains(bareName))
            return null;

        var executablePath = NormalizeExecutablePath(SafeResolve(
            _resolveExecutablePath,
            bareName));
        var packagedExecutable = executablePath is not null
            && IsWindowsAppsPath(executablePath);

        if (executablePath is not null && !packagedExecutable)
        {
            return new DockItem
            {
                Name = ResolveDesktopDisplayName(bareName, executablePath),
                Path = executablePath,
                IsBuiltIn = false,
                IsRunning = true,
            };
        }

        // 打包应用的 exe 不能假定可被直接重新启动；只有拿到 AUMID 才提供临时项。
        var aumid = SafeResolve(_resolveAumid, bareName);
        if (string.IsNullOrWhiteSpace(aumid))
            return null;

        var storeDisplayName = SafeResolve(_resolveStoreDisplayName, aumid);
        return new DockItem
        {
            Name = FirstNonEmpty(
                storeDisplayName,
                AppFriendlyNames.TryGetFriendlyName(bareName),
                bareName),
            Path = string.Empty,
            StoreAppName = bareName,
            IsBuiltIn = false,
            IsRunning = true,
        };
    }

    private string ResolveDesktopDisplayName(string bareName, string executablePath)
        => FirstNonEmpty(
            AppFriendlyNames.TryGetFriendlyName(bareName),
            SafeResolve(_resolveFileDescription, executablePath),
            bareName);

    private static string? NormalizeProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
            return null;

        var bareName = Path.GetFileNameWithoutExtension(processName.Trim());
        return string.IsNullOrWhiteSpace(bareName) ? null : bareName;
    }

    private static string? NormalizeExecutablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var fullPath = Path.GetFullPath(path);
            return Path.IsPathFullyQualified(fullPath) && File.Exists(fullPath)
                ? fullPath
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsWindowsAppsPath(string path)
    {
        var marker = $"{Path.DirectorySeparatorChar}WindowsApps{Path.DirectorySeparatorChar}";
        return path.Contains(marker, StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryResolveExecutablePath(string processName)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch (Exception)
        {
            return null;
        }

        try
        {
            foreach (var process in processes)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                        return path;
                }
                catch (Exception exception) when (exception is InvalidOperationException
                    or NotSupportedException
                    or System.ComponentModel.Win32Exception)
                {
                    // 受保护进程或枚举期间退出；继续尝试同名的其他实例。
                }
            }
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }

        return null;
    }

    private static string? TryResolveFileDescription(string executablePath)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(executablePath);
            return FirstNonEmpty(version.FileDescription, version.ProductName);
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? SafeResolve(Func<string, string?> resolver, string value)
    {
        try
        {
            return resolver(value);
        }
        catch (Exception)
        {
            // 运行图标只是增强信息；解析失败必须降级，不得影响 Hook 或 Dock 主路径。
            return null;
        }
    }

    private static string FirstNonEmpty(params string?[] candidates)
        => candidates
            .Select(static candidate => candidate?.Trim())
            .First(static candidate => !string.IsNullOrWhiteSpace(candidate))!;
}
