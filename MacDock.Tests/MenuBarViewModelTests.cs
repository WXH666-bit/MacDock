using System.Globalization;
using MacDock.Core.Services;
using MacDock.UI.ViewModels;
using Xunit;

namespace MacDock.Tests;

public class MenuBarViewModelTests
{
    private static readonly DateTime Sample = new(2026, 8, 23, 16, 38, 5);

    [Theory]
    [InlineData("notepad", "", "记事本")]
    [InlineData("notepad", "备份清单.txt", "记事本")]
    [InlineData("unknownapp", "我的窗口标题", "我的窗口标题")]
    [InlineData("unknownapp", "", "unknownapp")]
    [InlineData("dwm", "", "MacDock")]
    public void FormatAppName_Priority_FriendlyOverTitleOverProcess(
        string processName, string title, string expected)
    {
        // 映射表命中时即使有标题也优先友好名；未映射回退标题；再退化进程名；内部进程回兜底名
        Assert.Equal(expected, MenuBarViewModel.FormatAppName(processName, string.IsNullOrEmpty(title) ? null : title));
    }

    [Fact]
    public void FormatClock_ChineseCulture_UsesWeekdayMonthDayTime()
    {
        var text = MenuBarViewModel.FormatClock(Sample, new CultureInfo("zh-CN"));

        // 「周X M月d日 HH:mm」
        Assert.Equal("周日 8月23日 16:38", text);
    }

    [Fact]
    public void FormatClock_EnglishCulture_UsesCultureMonthDayPattern()
    {
        var culture = new CultureInfo("en-US");

        var text = MenuBarViewModel.FormatClock(Sample, culture);

        // 英文区域不应出现硬编码的中文「月/日」
        Assert.DoesNotContain("月", text);
        Assert.DoesNotContain("日", text);
        Assert.StartsWith(Sample.ToString("ddd", culture), text);
        Assert.EndsWith("16:38", text);
    }

    [Fact]
    public void FormatClock_UsesTwentyFourHourClock()
    {
        var evening = new DateTime(2026, 8, 23, 21, 5, 0);

        var text = MenuBarViewModel.FormatClock(evening, new CultureInfo("zh-CN"));

        Assert.EndsWith("21:05", text);
    }

    [Fact]
    public void FormatClock_NullCulture_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => MenuBarViewModel.FormatClock(Sample, null!));
    }
}
