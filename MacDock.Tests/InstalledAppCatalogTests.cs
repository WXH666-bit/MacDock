using MacDock.Core.Models;
using MacDock.Core.Services;
using Xunit;

namespace MacDock.Tests;

public sealed class InstalledAppCatalogTests
{
    [Fact]
    public async Task GetInstalledAppsAsync_DeduplicatesAndSortsDesktopAndStoreEntries()
    {
        var source = new FakeSource
        {
            Roots = ["current-user", "public"],
            ShortcutsByRoot = new Dictionary<string, IEnumerable<string>>
            {
                ["current-user"] = ["Beta.lnk", "Duplicate.lnk"],
                ["public"] = ["Alpha.lnk", "Duplicate-public.lnk"],
            },
            ShortcutResults = new Dictionary<string, ShortcutInfo>
            {
                ["Beta.lnk"] = new(@"C:\Apps\Beta.exe", @"C:\Apps\Beta.exe", "--profile beta"),
                ["Duplicate.lnk"] = new(@"C:\Apps\Duplicate.exe", @"C:\Apps\Duplicate.ico", null),
                ["Alpha.lnk"] = new(@"C:\Apps\Alpha.exe", @"C:\Apps\Alpha.exe", null),
                ["Duplicate-public.lnk"] = new(@"c:\apps\duplicate.exe", @"C:\Apps\Duplicate.ico", null),
            },
            StorePackages =
            [
                new InstalledAppStorePackage(
                    "store-b",
                    _ =>
                    [
                        new InstalledAppStoreEntry("Store Beta", "Contoso.Beta!App"),
                    ]),
                new InstalledAppStorePackage(
                    "store-a",
                    _ =>
                    [
                        new InstalledAppStoreEntry(
                            "Store Alpha",
                            "Contoso.Alpha!App",
                            @"C:\Packages\Alpha.png"),
                        new InstalledAppStoreEntry(
                            "Store Alpha Duplicate",
                            "contoso.alpha!app"),
                    ]),
            ],
        };

        var apps = await new InstalledAppCatalog(source).GetInstalledAppsAsync();

        Assert.Equal(
            ["Alpha", "Beta", "Duplicate", "Store Alpha", "Store Beta"],
            apps.Select(static app => app.Name));
        Assert.Equal(5, apps.Count);

        var desktop = Assert.Single(apps, static app => app.Kind == InstalledAppKind.Desktop
            && app.LaunchTarget.EndsWith("Duplicate.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(@"C:\Apps\Duplicate.ico", desktop.IconPath);

        var store = Assert.Single(apps, static app => app.Kind == InstalledAppKind.Store
            && app.Aumid == "Contoso.Alpha!App");
        Assert.Equal(@"C:\Packages\Alpha.png", store.IconPath);
        Assert.Equal("Contoso.Alpha!App", store.LaunchTarget);
    }

    [Fact]
    public async Task GetInstalledAppsAsync_SkipsDirectoryShortcutAndPackageFailures()
    {
        var errors = new List<Exception>();
        var source = new FakeSource
        {
            Roots = ["good", "bad-directory"],
            ShortcutsByRoot = new Dictionary<string, IEnumerable<string>>
            {
                ["good"] = ["Good.lnk", "Broken.lnk"],
            },
            DirectoryErrors = new HashSet<string>(StringComparer.Ordinal) { "bad-directory" },
            ShortcutResults = new Dictionary<string, ShortcutInfo>
            {
                ["Good.lnk"] = new(@"C:\Apps\Good.exe", @"C:\Apps\Good.exe", "--safe"),
            },
            ShortcutErrors = new HashSet<string>(StringComparer.Ordinal) { "Broken.lnk" },
            StorePackages =
            [
                new InstalledAppStorePackage(
                    "good-package",
                    _ => [new InstalledAppStoreEntry("Good Store", "Good.Package!App")]),
                new InstalledAppStorePackage(
                    "broken-package",
                    _ => throw new InvalidOperationException("package unavailable")),
            ],
        };

        var apps = await new InstalledAppCatalog(source, errors.Add).GetInstalledAppsAsync();

        Assert.Equal(["Good", "Good Store"], apps.Select(static app => app.Name));
        Assert.Equal("--safe", apps[0].Arguments);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public async Task GetInstalledAppsAsync_HonorsCancellationWithoutTouchingSystemSource()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var source = new FakeSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new InstalledAppCatalog(source).GetInstalledAppsAsync(cancellation.Token));

        Assert.False(source.WasCalled);
    }

    [Fact]
    public async Task GetInstalledAppsAsync_HonorsCancellationBetweenInjectedItems()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new FakeSource
        {
            Roots = ["memory"],
            ShortcutsByRoot = new Dictionary<string, IEnumerable<string>>
            {
                ["memory"] = ["First.lnk", "Second.lnk"],
            },
            ShortcutResults = new Dictionary<string, ShortcutInfo>
            {
                ["First.lnk"] = new(@"C:\Apps\First.exe", @"C:\Apps\First.exe", null),
                ["Second.lnk"] = new(@"C:\Apps\Second.exe", @"C:\Apps\Second.exe", null),
            },
            ResolveShortcutOverride = path =>
            {
                if (path == "First.lnk")
                    cancellation.Cancel();

                return new ShortcutInfo(
                    path == "First.lnk" ? @"C:\Apps\First.exe" : @"C:\Apps\Second.exe",
                    path,
                    null);
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new InstalledAppCatalog(source).GetInstalledAppsAsync(cancellation.Token));
    }

    [Fact]
    public void NormalizeAndSort_IsPureAndDeduplicatesByTheLaunchIdentity()
    {
        var candidates = new[]
        {
            new InstalledApp("Zulu", InstalledAppKind.Desktop, @"C:\Apps\zulu.exe"),
            new InstalledApp("alpha", InstalledAppKind.Desktop, @"c:\apps\alpha.exe"),
            new InstalledApp("Alpha duplicate", InstalledAppKind.Desktop, @"C:\Apps\ALPHA.EXE"),
            new InstalledApp("Store", InstalledAppKind.Store, "Contoso.App!App"),
            new InstalledApp("Store duplicate", InstalledAppKind.Store, "contoso.app!app"),
        };

        var apps = InstalledAppCatalog.NormalizeAndSort(candidates);

        Assert.Equal(["alpha", "Store", "Zulu"], apps.Select(static app => app.Name));
        Assert.Equal(3, apps.Count);
    }

    private sealed class FakeSource : IInstalledAppCatalogSource
    {
        public IEnumerable<string> Roots { get; init; } = [];

        public IReadOnlyDictionary<string, IEnumerable<string>> ShortcutsByRoot { get; init; }
            = new Dictionary<string, IEnumerable<string>>();

        public IReadOnlySet<string> DirectoryErrors { get; init; }
            = new HashSet<string>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, ShortcutInfo> ShortcutResults { get; init; }
            = new Dictionary<string, ShortcutInfo>(StringComparer.Ordinal);

        public IReadOnlySet<string> ShortcutErrors { get; init; }
            = new HashSet<string>(StringComparer.Ordinal);

        public IReadOnlyList<InstalledAppStorePackage> StorePackages { get; init; } = [];

        public Func<string, ShortcutInfo>? ResolveShortcutOverride { get; init; }

        public bool WasCalled { get; private set; }

        public IEnumerable<string> GetStartMenuRoots()
        {
            WasCalled = true;
            return Roots;
        }

        public IEnumerable<string> EnumerateShortcutFiles(
            string directory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DirectoryErrors.Contains(directory))
                throw new IOException("directory unavailable");

            return ShortcutsByRoot.TryGetValue(directory, out var shortcuts)
                ? shortcuts
                : [];
        }

        public ShortcutInfo ResolveShortcut(string shortcutPath)
        {
            if (ShortcutErrors.Contains(shortcutPath))
                throw new InvalidDataException("shortcut unavailable");

            if (ResolveShortcutOverride is not null)
                return ResolveShortcutOverride(shortcutPath);

            return ShortcutResults[shortcutPath];
        }

        public IEnumerable<InstalledAppStorePackage> EnumerateStorePackages(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return StorePackages;
        }
    }
}
