using MacDock.Core.Services;
using Xunit;

namespace MacDock.Tests;

public class ShortcutResolverTests
{
    [Fact]
    public void IsShortcut_DetectsLnk()
    {
        Assert.True(ShortcutResolver.IsShortcut(@"C:\foo.lnk"));
        Assert.True(ShortcutResolver.IsShortcut(@"C:\dir\app.LNK"));
        Assert.False(ShortcutResolver.IsShortcut(@"C:\foo.exe"));
        Assert.False(ShortcutResolver.IsShortcut(@"C:\foo.txt"));
    }

    [Fact]
    public void Resolve_NonShortcut_ReturnsSamePath()
    {
        var result = ShortcutResolver.Resolve(@"C:\Windows\System32\notepad.exe");

        Assert.Equal(@"C:\Windows\System32\notepad.exe", result.TargetPath);
        Assert.Equal(@"C:\Windows\System32\notepad.exe", result.IconPath);
    }
}
