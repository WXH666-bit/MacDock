using MacDock.Core.Models;
using MacDock.UI.ViewModels;
using Xunit;

namespace MacDock.Tests;

public sealed class LaunchpadSearchTests
{
    private static readonly InstalledApp[] Apps =
    [
        new("Visual Studio", InstalledAppKind.Desktop, @"C:\VS.exe"),
        new("Studio One", InstalledAppKind.Desktop, @"C:\Studio.exe"),
        new("Windows Terminal", InstalledAppKind.Desktop, @"C:\Terminal.exe"),
        new("计算器", InstalledAppKind.Store, "Calculator!App"),
    ];

    [Fact]
    public void Filter_EmptyQuery_PreservesCatalogOrder()
    {
        Assert.Equal(Apps, LaunchpadSearch.Filter(Apps, "  "));
    }

    [Fact]
    public void Filter_RanksPrefixBeforeContainsBeforeSubsequence()
    {
        var result = LaunchpadSearch.Filter(Apps, "stu");

        Assert.Equal(["Studio One", "Visual Studio"], result.Select(static app => app.Name));
    }

    [Fact]
    public void Filter_SubsequenceIgnoresSpacesAndCase()
    {
        var result = LaunchpadSearch.Filter(Apps, "w t");

        Assert.Equal("Windows Terminal", Assert.Single(result).Name);
    }

    [Fact]
    public void Filter_ChineseNameMatchesDirectCharacters()
    {
        var result = LaunchpadSearch.Filter(Apps, "算器");

        Assert.Equal("计算器", Assert.Single(result).Name);
    }

    [Fact]
    public void Filter_NoMatch_ReturnsEmpty()
    {
        Assert.Empty(LaunchpadSearch.Filter(Apps, "not-installed"));
    }
}
