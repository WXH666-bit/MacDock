using MacDock.Core.Services;
using MacDock.Core.Models;
using MacDock.UI.ViewModels;
using Xunit;

namespace MacDock.Tests;

public sealed class RunningDockItemResolverTests
{
    [Fact]
    public void Resolve_DesktopProcessBuildsLaunchableTransientItem()
    {
        var executablePath = Path.Combine(
            Path.GetTempPath(),
            $"sample-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(executablePath, [0]);
        try
        {
            var resolver = new RunningDockItemResolver(
                _ => executablePath,
                _ => null,
                _ => null,
                _ => "Sample Product");

            var item = resolver.Resolve("sample.exe");

            Assert.NotNull(item);
            Assert.Equal("Sample Product", item.Name);
            Assert.Equal(executablePath, item.Path, ignoreCase: true);
            Assert.Null(item.StoreAppName);
            Assert.True(item.IsRunning);
            Assert.False(item.IsBuiltIn);
        }
        finally
        {
            File.Delete(executablePath);
        }
    }

    [Fact]
    public void Resolve_StoreProcessUsesAumidBackedLaunchIdentity()
    {
        var resolver = new RunningDockItemResolver(
            _ => null,
            _ => "Sample.Package!App",
            _ => "商店示例",
            _ => null);

        var item = resolver.Resolve("sample-store");

        Assert.NotNull(item);
        Assert.Equal("商店示例", item.Name);
        Assert.Equal(string.Empty, item.Path);
        Assert.Equal("sample-store", item.StoreAppName);
        Assert.True(item.IsRunning);
    }

    [Fact]
    public void Resolve_UnlaunchableProcessReturnsNull()
    {
        var resolver = new RunningDockItemResolver(
            _ => null,
            _ => null,
            _ => null,
            _ => null);

        Assert.Null(resolver.Resolve("unknown-process"));
    }

    [Fact]
    public void SelectPersistentItems_ExcludesTransientRunningItems()
    {
        static void Ignore(DockItemViewModel _)
        {
        }

        var pinnedModel = new MacDock.Core.Models.DockItem
        {
            Name = "Pinned",
            Path = @"C:\Apps\Pinned.exe",
        };
        var transientModel = new MacDock.Core.Models.DockItem
        {
            Name = "Transient",
            Path = @"C:\Apps\Transient.exe",
            IsRunning = true,
        };
        var pinned = new DockItemViewModel(
            pinnedModel,
            icon: null,
            isPinned: true,
            Ignore,
            Ignore,
            Ignore);
        var transient = new DockItemViewModel(
            transientModel,
            icon: null,
            isPinned: false,
            Ignore,
            Ignore,
            Ignore);

        var persisted = MainViewModel.SelectPersistentItems([pinned, transient]);

        var item = Assert.Single(persisted);
        Assert.Same(pinnedModel, item);
    }

    [Fact]
    public void SelectPersistentItems_IncludesPinnedSeparatorInOrder()
    {
        static void Ignore(DockItemViewModel _)
        {
        }

        var firstModel = new DockItem { Name = "First", Path = @"C:\Apps\First.exe" };
        var separatorModel = new DockItem
        {
            Kind = DockItemKind.Separator,
            Name = "分隔线",
        };
        var transientModel = new DockItem
        {
            Name = "Transient",
            Path = @"C:\Apps\Transient.exe",
        };
        var first = new DockItemViewModel(firstModel, null, true, Ignore, Ignore, Ignore);
        var separator = new DockItemViewModel(
            separatorModel,
            null,
            true,
            Ignore,
            Ignore,
            Ignore);
        var transient = new DockItemViewModel(
            transientModel,
            null,
            false,
            Ignore,
            Ignore,
            Ignore);

        var persisted = MainViewModel.SelectPersistentItems(
            [first, separator, transient]);

        Assert.Equal([firstModel, separatorModel], persisted);
    }

    [Theory]
    [InlineData("ApplicationFrameHost")]
    [InlineData("RuntimeBroker.exe")]
    [InlineData("TextInputHost")]
    public void Resolve_GenericWindowsHostReturnsNull(string processName)
    {
        var resolverCalls = 0;
        var resolver = new RunningDockItemResolver(
            _ =>
            {
                resolverCalls++;
                return null;
            },
            _ =>
            {
                resolverCalls++;
                return null;
            },
            _ => null,
            _ => null);

        Assert.Null(resolver.Resolve(processName));
        Assert.Equal(0, resolverCalls);
    }
}
