using System.Globalization;
using MacDock.UI.ViewModels;
using Xunit;

namespace MacDock.Tests;

public class MenuBarViewModelTests
{
    private static readonly DateTime Sample = new(2026, 8, 23, 16, 38, 5);

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
