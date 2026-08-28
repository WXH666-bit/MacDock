using MacDock.Core.Models;
using MacDock.Core.Services;
using Xunit;

namespace MacDock.Tests;

public class DockItemStoreTests
{
    [Fact]
    public void DefaultItems_ContainsBuiltInItems()
    {
        var defaults = DockItemStore.DefaultItems;

        Assert.NotEmpty(defaults);
        Assert.Contains(defaults, d => d.IsBuiltIn);
        Assert.Contains(defaults, d => d.Name == "记事本");
        Assert.Contains(defaults, d => d.Name == "资源管理器");
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        // 路径必须真实存在，否则会被 Load 的配置自愈判定为坏死条目
        var path = Path.Combine(Path.GetTempPath(), $"macdock-test-{Guid.NewGuid():N}.json");
        var appA = Path.Combine(Path.GetTempPath(), $"macdock-app-{Guid.NewGuid():N}.exe");
        var appB = Path.Combine(Path.GetTempPath(), $"macdock-app-{Guid.NewGuid():N}.exe");
        File.WriteAllText(appA, string.Empty);
        File.WriteAllText(appB, string.Empty);
        try
        {
            var store = new DockItemStore(path);
            var items = new List<DockItem>
            {
                new() { Name = "记事本", Path = appA, IsBuiltIn = true, IconPath = "icon" },
                new() { Name = "自定义", Path = appB, IsBuiltIn = false, Arguments = "--foo" },
            };

            store.Save(items);

            var loaded = store.Load();
            Assert.Equal(2, loaded.Count);
            Assert.Equal("记事本", loaded[0].Name);
            Assert.Equal(appA, loaded[0].Path);
            Assert.True(loaded[0].IsBuiltIn);
            Assert.Equal("--foo", loaded[1].Arguments);
        }
        finally
        {
            foreach (var f in new[] { path, appA, appB })
            {
                if (File.Exists(f))
                    File.Delete(f);
            }
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"macdock-missing-{Guid.NewGuid():N}.json");
        var store = new DockItemStore(path);

        var loaded = store.Load();

        Assert.NotEmpty(loaded);
        Assert.Contains(loaded, d => d.IsBuiltIn);
    }

    [Fact]
    public void Load_ValidEmptyList_ReturnsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"macdock-empty-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "[]");

            var loaded = new DockItemStore(path).Load();

            Assert.Empty(loaded);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_DeadItemMatchingDefaultName_IsReplacedByDefault()
    {
        var expected = DockItemStore.DefaultItems.First(d => d.Name == "资源管理器");
        var path = Path.Combine(Path.GetTempPath(), $"macdock-heal-{Guid.NewGuid():N}.json");
        try
        {
            var store = new DockItemStore(path);
            // 旧版本写入的坏死条目：死路径 + 无 StoreAppName + 无 IconOverride
            store.Save(new List<DockItem>
            {
                new() { Name = "资源管理器", Path = @"C:\Nope\gone.exe", IsBuiltIn = true },
            });

            var loaded = store.Load();

            var item = Assert.Single(loaded);
            Assert.Equal(expected.Name, item.Name);
            Assert.Equal(expected.Path, item.Path);
            Assert.Equal(expected.IconOverride, item.IconOverride);
            Assert.Equal(expected.StoreAppName, item.StoreAppName);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_UserItemWithIconOverride_IsKeptEvenIfPathMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"macdock-heal-{Guid.NewGuid():N}.json");
        try
        {
            var store = new DockItemStore(path);
            store.Save(new List<DockItem>
            {
                new()
                {
                    Name = "我的工具",
                    Path = @"C:\Nope\tool.exe",
                    IconOverride = "pack://application:,,,/Assets/Icons/safari.png",
                },
            });

            var loaded = store.Load();

            var item = Assert.Single(loaded);
            Assert.Equal("我的工具", item.Name);
            Assert.Equal(@"C:\Nope\tool.exe", item.Path);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Load_AllItemsDeadAndUnmatched_ReturnsDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"macdock-heal-{Guid.NewGuid():N}.json");
        try
        {
            var store = new DockItemStore(path);
            store.Save(new List<DockItem>
            {
                new() { Name = "不存在的应用A", Path = @"C:\Nope\a.exe" },
                new() { Name = "不存在的应用B", Path = string.Empty },
            });

            var loaded = store.Load();

            Assert.Equal(
                DockItemStore.DefaultItems.Select(d => d.Name),
                loaded.Select(d => d.Name));
            Assert.Equal(
                DockItemStore.DefaultItems.Select(d => d.Path),
                loaded.Select(d => d.Path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void Save_DoesNotPersistIsRunning()
    {
        var path = Path.Combine(Path.GetTempPath(), $"macdock-running-{Guid.NewGuid():N}.json");
        try
        {
            new DockItemStore(path).Save(new List<DockItem>
            {
                new() { Name = "记事本", Path = @"C:\Windows\System32\notepad.exe", IsRunning = true },
            });

            var json = File.ReadAllText(path);

            Assert.DoesNotContain("isRunning", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void DefaultItems_AllPathsExist()
    {
        foreach (var item in DockItemStore.DefaultItems)
        {
            // URL 型默认项（浏览器回退）无本地路径，跳过
            if (Uri.TryCreate(item.Path, UriKind.Absolute, out _))
                continue;
            Assert.True(File.Exists(item.Path), $"默认项 {item.Name} 路径不存在：{item.Path}");
        }
    }
}
