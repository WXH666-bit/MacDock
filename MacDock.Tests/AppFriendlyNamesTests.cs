using MacDock.Core.Services;
using Xunit;

namespace MacDock.Tests;

/// <summary>AppFriendlyNames 映射：大小写不敏感、带/不带扩展名均命中、未命中返回 null。</summary>
public sealed class AppFriendlyNamesTests
{
    [Theory]
    [InlineData("notepad", "记事本")]
    [InlineData("notepad.exe", "记事本")]
    [InlineData("NOTEPAD", "记事本")]
    [InlineData("EXCEL", "Microsoft Excel")]
    [InlineData("WINWORD", "Microsoft Word")]
    [InlineData("explorer", "文件资源管理器")]
    [InlineData("WeChat", "微信")]
    [InlineData("cloudmusic", "网易云音乐")]
    [InlineData("mstsc", "远程桌面连接")]
    public void TryGetFriendlyName_CommonProcesses_ReturnsChinese(string processName, string expected)
    {
        Assert.Equal(expected, AppFriendlyNames.TryGetFriendlyName(processName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknownapp")]
    [InlineData("mycustom_editor")]
    public void TryGetFriendlyName_Unknown_ReturnsNull(string? processName)
    {
        Assert.Null(AppFriendlyNames.TryGetFriendlyName(processName));
    }

    [Fact]
    public void IsIgnored_Dwm_ReturnsTrue()
    {
        Assert.True(AppFriendlyNames.IsIgnored("dwm"));
        Assert.True(AppFriendlyNames.IsIgnored("DWM.EXE"));
    }

    [Fact]
    public void IsIgnored_NormalProcess_ReturnsFalse()
    {
        Assert.False(AppFriendlyNames.IsIgnored("notepad"));
    }
}
