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
        var path = Path.Combine(Path.GetTempPath(), $"macdock-test-{Guid.NewGuid():N}.json");
        try
        {
            var store = new DockItemStore(path);
            var items = new List<DockItem>
            {
                new() { Name = "记事本", Path = @"C:\Windows\System32\notepad.exe", IsBuiltIn = true, IconPath = "icon" },
                new() { Name = "自定义", Path = @"C:\Apps\tool.exe", IsBuiltIn = false, Arguments = "--foo" },
            };

            store.Save(items);

            var loaded = store.Load();
            Assert.Equal(2, loaded.Count);
            Assert.Equal("记事本", loaded[0].Name);
            Assert.Equal(@"C:\Windows\System32\notepad.exe", loaded[0].Path);
            Assert.True(loaded[0].IsBuiltIn);
            Assert.Equal("--foo", loaded[1].Arguments);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
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
