using MacDock.Core.Models;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace MacDock.Core.Services;

/// <summary>
/// 一个商店包中可启动的应用条目。它是注入边界上的纯数据，不把 WinRT 类型泄漏给测试或 UI。
/// </summary>
public sealed record InstalledAppStoreEntry(
    string Name,
    string Aumid,
    string? IconPath = null,
    string? Arguments = null);

/// <summary>
/// 一个商店包的延迟读取描述。延迟读取使单个包失败时可以跳过该包并继续处理其他包。
/// </summary>
public sealed record InstalledAppStorePackage(
    string Identity,
    Func<CancellationToken, IEnumerable<InstalledAppStoreEntry>> EnumerateEntries);

/// <summary>
/// 已安装应用目录的数据源注入边界。默认实现访问 Windows 开始菜单和当前用户包目录；
/// 测试可以完全使用内存实现，不访问真实系统。
/// </summary>
public interface IInstalledAppCatalogSource
{
    /// <summary>返回当前用户和公共开始菜单根目录。</summary>
    IEnumerable<string> GetStartMenuRoots();

    /// <summary>枚举一个开始菜单目录树中的快捷方式路径。</summary>
    IEnumerable<string> EnumerateShortcutFiles(
        string directory,
        CancellationToken cancellationToken);

    /// <summary>解析单个快捷方式。</summary>
    ShortcutInfo ResolveShortcut(string shortcutPath);

    /// <summary>枚举当前用户的商店包；每个包的条目通过包描述延迟读取。</summary>
    IEnumerable<InstalledAppStorePackage> EnumerateStorePackages(
        CancellationToken cancellationToken);
}

/// <summary>
/// 已安装应用目录服务。所有系统枚举在后台任务中执行，单个目录、快捷方式或商店包失败
/// 只会丢弃对应局部结果，不会使整批目录失败。
/// </summary>
public sealed class InstalledAppCatalog
{
    private readonly IInstalledAppCatalogSource _source;
    private readonly Action<Exception>? _errorSink;

    /// <summary>创建目录服务；不传数据源时使用 Windows 默认数据源。</summary>
    public InstalledAppCatalog(
        IInstalledAppCatalogSource? source = null,
        Action<Exception>? errorSink = null)
    {
        _source = source ?? new WindowsInstalledAppCatalogSource();
        _errorSink = errorSink;
    }

    /// <summary>
    /// 在后台枚举当前用户可用的桌面和商店应用。取消会保留取消语义并抛出取消异常。
    /// </summary>
    public Task<IReadOnlyList<InstalledApp>> GetInstalledAppsAsync(
        CancellationToken cancellationToken = default)
        => Task.Run(() => BuildCatalog(cancellationToken), cancellationToken);

    /// <summary>GetInstalledAppsAsync 的语义别名，便于启动台服务调用。</summary>
    public Task<IReadOnlyList<InstalledApp>> LoadAsync(
        CancellationToken cancellationToken = default)
        => GetInstalledAppsAsync(cancellationToken);

    /// <summary>
    /// 对已读取的候选应用做纯逻辑清洗、去重和稳定排序；不访问文件系统、Shell 或包管理器。
    /// 桌面应用按目标路径和参数去重，商店应用按 AUMID 去重。
    /// </summary>
    public static IReadOnlyList<InstalledApp> NormalizeAndSort(
        IEnumerable<InstalledApp> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var unique = new Dictionary<InstalledAppKey, InstalledApp>(InstalledAppKeyComparer.Instance);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var normalized = NormalizeCandidate(candidate);
            if (normalized is null)
                continue;

            var key = InstalledAppKey.Create(normalized);
            if (!unique.TryGetValue(key, out var existing)
                || ComparePreference(normalized, existing) < 0)
            {
                unique[key] = normalized;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        return unique.Values
            .OrderBy(static app => app, InstalledAppComparer.Instance)
            .ToArray();
    }

    private IReadOnlyList<InstalledApp> BuildCatalog(CancellationToken cancellationToken)
    {
        var candidates = new List<InstalledApp>();
        AddDesktopCandidates(candidates, cancellationToken);
        AddStoreCandidates(candidates, cancellationToken);
        return NormalizeAndSort(candidates, cancellationToken);
    }

    private void AddDesktopCandidates(
        ICollection<InstalledApp> candidates,
        CancellationToken cancellationToken)
    {
        IEnumerable<string>? roots;
        try
        {
            roots = _source.GetStartMenuRoots();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            ReportError(exception);
            return;
        }

        if (roots is null)
            return;

        try
        {
            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(root))
                    continue;

                AddDesktopDirectoryCandidates(candidates, root, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 根目录枚举器本身失败时保留已经读到的根目录结果。
            ReportError(exception);
        }
    }

    private void AddDesktopDirectoryCandidates(
        ICollection<InstalledApp> candidates,
        string root,
        CancellationToken cancellationToken)
    {
        IEnumerable<string>? shortcuts;
        try
        {
            shortcuts = _source.EnumerateShortcutFiles(root, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 单个开始菜单目录不可读时跳过该目录，继续下一个根目录。
            ReportError(exception);
            return;
        }

        if (shortcuts is null)
            return;

        try
        {
            foreach (var shortcutPath in shortcuts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(shortcutPath)
                    || !ShortcutResolver.IsShortcut(shortcutPath))
                {
                    continue;
                }

                try
                {
                    var shortcut = _source.ResolveShortcut(shortcutPath);
                    if (IsUnresolvedShortcut(shortcutPath, shortcut))
                        continue;

                    var name = GetShortcutName(shortcutPath);
                    if (name.Length == 0 || string.IsNullOrWhiteSpace(shortcut.TargetPath))
                        continue;

                    candidates.Add(new InstalledApp(
                        name,
                        InstalledAppKind.Desktop,
                        shortcut.TargetPath,
                        shortcut.IconPath,
                        shortcut.Arguments));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // 单个快捷方式损坏或解析失败时跳过，不影响同目录其他快捷方式。
                    ReportError(exception);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 延迟目录枚举器可能在中途失败；保留已读取项目并结束该目录。
            ReportError(exception);
        }
    }

    private void AddStoreCandidates(
        ICollection<InstalledApp> candidates,
        CancellationToken cancellationToken)
    {
        IEnumerable<InstalledAppStorePackage>? packages;
        try
        {
            packages = _source.EnumerateStorePackages(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            ReportError(exception);
            return;
        }

        if (packages is null)
            return;

        try
        {
            foreach (var package in packages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var entries = package.EnumerateEntries(cancellationToken);
                    if (entries is null)
                        continue;

                    foreach (var entry in entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (string.IsNullOrWhiteSpace(entry.Name)
                            || string.IsNullOrWhiteSpace(entry.Aumid))
                        {
                            continue;
                        }

                        candidates.Add(new InstalledApp(
                            entry.Name,
                            InstalledAppKind.Store,
                            entry.Aumid,
                            entry.IconPath,
                            entry.Arguments));
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // 单个包（包括其 AppListEntry 读取）失败时跳过该包。
                    ReportError(exception);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // 包集合本身中途失败时保留已经读取的包结果。
            ReportError(exception);
        }
    }

    private void ReportError(Exception exception)
    {
        try
        {
            _errorSink?.Invoke(exception);
        }
        catch
        {
            // 诊断回调不能改变目录服务的 fail-closed 行为。
        }
    }

    private static bool IsUnresolvedShortcut(string shortcutPath, ShortcutInfo shortcut)
    {
        return string.Equals(
            shortcutPath.Trim(),
            shortcut.TargetPath?.Trim(),
            StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                shortcutPath.Trim(),
                shortcut.IconPath?.Trim(),
                StringComparison.OrdinalIgnoreCase);
    }

    private static string GetShortcutName(string shortcutPath)
    {
        try
        {
            return Path.GetFileNameWithoutExtension(shortcutPath).Trim();
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private static InstalledApp? NormalizeCandidate(InstalledApp candidate)
    {
        if (!Enum.IsDefined(candidate.Kind))
            return null;

        var name = candidate.Name?.Trim();
        var target = candidate.LaunchTarget?.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(target))
            return null;

        return candidate with
        {
            Name = name,
            LaunchTarget = target,
            IconPath = NormalizeOptional(candidate.IconPath),
            Arguments = NormalizeOptional(candidate.Arguments),
        };
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int ComparePreference(InstalledApp left, InstalledApp right)
    {
        var leftQuality = GetMetadataQuality(left);
        var rightQuality = GetMetadataQuality(right);
        if (leftQuality != rightQuality)
            return rightQuality.CompareTo(leftQuality);

        return InstalledAppComparer.Instance.Compare(left, right);
    }

    private static int GetMetadataQuality(InstalledApp app)
    {
        var quality = 0;
        if (!string.IsNullOrWhiteSpace(app.IconPath))
            quality++;
        if (!string.IsNullOrWhiteSpace(app.Arguments))
            quality++;
        return quality;
    }

    private readonly record struct InstalledAppKey(
        InstalledAppKind Kind,
        string Target,
        string Arguments)
    {
        public static InstalledAppKey Create(InstalledApp app)
        {
            var target = app.Kind == InstalledAppKind.Store
                ? app.LaunchTarget.Trim()
                : NormalizeDesktopTarget(app.LaunchTarget);
            var arguments = app.Kind == InstalledAppKind.Desktop
                ? app.Arguments ?? string.Empty
                : string.Empty;
            return new InstalledAppKey(app.Kind, target, arguments);
        }
    }

    private sealed class InstalledAppKeyComparer : IEqualityComparer<InstalledAppKey>
    {
        public static InstalledAppKeyComparer Instance { get; } = new();

        public bool Equals(InstalledAppKey x, InstalledAppKey y)
            => x.Kind == y.Kind
                && StringComparer.OrdinalIgnoreCase.Equals(x.Target, y.Target)
                && StringComparer.OrdinalIgnoreCase.Equals(x.Arguments, y.Arguments);

        public int GetHashCode(InstalledAppKey obj)
            => HashCode.Combine(
                obj.Kind,
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Target),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Arguments));
    }

    private sealed class InstalledAppComparer : IComparer<InstalledApp>
    {
        public static InstalledAppComparer Instance { get; } = new();

        public int Compare(InstalledApp? x, InstalledApp? y)
        {
            if (ReferenceEquals(x, y))
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;

            var result = CompareString(x.Name, y.Name);
            if (result != 0)
                return result;

            result = x.Kind.CompareTo(y.Kind);
            if (result != 0)
                return result;

            result = CompareString(x.LaunchTarget, y.LaunchTarget);
            if (result != 0)
                return result;

            result = CompareString(x.Arguments, y.Arguments);
            if (result != 0)
                return result;

            return CompareString(x.IconPath, y.IconPath);
        }

        private static int CompareString(string? left, string? right)
        {
            var result = StringComparer.OrdinalIgnoreCase.Compare(left ?? string.Empty, right ?? string.Empty);
            return result != 0
                ? result
                : StringComparer.Ordinal.Compare(left ?? string.Empty, right ?? string.Empty);
        }
    }

    private static string NormalizeDesktopTarget(string target)
    {
        var trimmed = target.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !uri.IsFile)
            return trimmed;

        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch (Exception)
        {
            return trimmed;
        }
    }

    private sealed class WindowsInstalledAppCatalogSource : IInstalledAppCatalogSource
    {
        public IEnumerable<string> GetStartMenuRoots()
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        }

        public IEnumerable<string> EnumerateShortcutFiles(
            string directory,
            CancellationToken cancellationToken)
            => EnumerateShortcutFilesSafely(directory, cancellationToken);

        public ShortcutInfo ResolveShortcut(string shortcutPath)
            => ShortcutResolver.Resolve(shortcutPath);

        public IEnumerable<InstalledAppStorePackage> EnumerateStorePackages(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<Package> packages;
            try
            {
                // 空 SID 是 WinRT 约定的“当前用户”，避免请求其他用户权限。
                packages = new PackageManager()
                    .FindPackagesForUser(string.Empty)
                    .ToList();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                yield break;
            }

            foreach (var package in packages)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string? identity;
                try
                {
                    identity = package.Id.FullName;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(identity))
                    continue;

                yield return new InstalledAppStorePackage(
                    identity,
                    token => EnumeratePackageEntries(package, token));
            }
        }

        private static IEnumerable<InstalledAppStoreEntry> EnumeratePackageEntries(
            Package package,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<Windows.ApplicationModel.Core.AppListEntry>? entries;
            try
            {
                // 该同步等待只发生在 InstalledAppCatalog 的后台线程中，不阻塞 UI Dispatcher。
                entries = package.GetAppListEntriesAsync().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                yield break;
            }

            if (entries is null)
                yield break;

            var iconPath = TryGetPackageIconPath(package);
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string? name;
                string? aumid;
                try
                {
                    name = entry.DisplayInfo.DisplayName;
                    aumid = entry.AppUserModelId;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(aumid))
                    continue;

                yield return new InstalledAppStoreEntry(name, aumid, iconPath);
            }
        }

        private static string? TryGetPackageIconPath(Package package)
        {
            try
            {
                var packageRoot = package.InstalledPath;
                var logo = package.Logo;
                if (string.IsNullOrWhiteSpace(packageRoot) || logo is null)
                    return null;

                if (logo.IsFile)
                    return PackageAssetResolver.ResolveLogoPath(packageRoot, logo.LocalPath);

                if (!string.Equals(logo.Scheme, "ms-appx", StringComparison.OrdinalIgnoreCase))
                    return null;

                var relativePath = Uri.UnescapeDataString(logo.AbsolutePath)
                    .TrimStart('/', '\\');
                if (relativePath.Length == 0)
                    return null;

                return PackageAssetResolver.ResolveLogoPath(packageRoot, relativePath);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static IEnumerable<string> EnumerateShortcutFilesSafely(
            string directory,
            CancellationToken cancellationToken)
        {
            if (!TryGetFullPath(directory, out var root)
                || !TryDirectoryExists(root))
            {
                yield break;
            }

            var pending = new Stack<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            pending.Push(root);

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = pending.Pop();
                if (!visited.Add(current))
                    continue;

                foreach (var file in TryEnumerateFiles(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return file;
                }

                var directories = TryEnumerateDirectories(current);
                for (var index = directories.Length - 1; index >= 0; index--)
                {
                    var child = directories[index];
                    if (TryIsReparsePoint(child))
                        continue;

                    if (TryGetFullPath(child, out var childPath))
                        pending.Push(childPath);
                }
            }
        }

        private static string[] TryEnumerateFiles(string directory)
        {
            try
            {
                return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                    .Where(ShortcutResolver.IsShortcut)
                    .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static path => path, StringComparer.Ordinal)
                    .ToArray();
            }
            catch
            {
                return [];
            }
        }

        private static string[] TryEnumerateDirectories(string directory)
        {
            try
            {
                return Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly)
                    .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static path => path, StringComparer.Ordinal)
                    .ToArray();
            }
            catch
            {
                return [];
            }
        }

        private static bool TryDirectoryExists(string directory)
        {
            try
            {
                return Directory.Exists(directory);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryIsReparsePoint(string path)
        {
            try
            {
                return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
            }
            catch
            {
                return true;
            }
        }

        private static bool TryGetFullPath(string path, out string fullPath)
        {
            try
            {
                fullPath = Path.GetFullPath(path);
                return true;
            }
            catch (ArgumentException)
            {
                fullPath = string.Empty;
                return false;
            }
            catch (NotSupportedException)
            {
                fullPath = string.Empty;
                return false;
            }
        }
    }
}
