using MacDock.Core.Services;
using Xunit;

namespace MacDock.Tests;

public sealed class SystemSettingsLauncherTests
{
    [Theory]
    [InlineData(SystemSettingsPage.Wifi, "ms-settings:network-wifi")]
    [InlineData(SystemSettingsPage.Bluetooth, "ms-settings:bluetooth")]
    [InlineData(SystemSettingsPage.FocusAssist, "ms-settings:quiethours")]
    public void GetUri_UsesWhitelistedWindowsSettingsPage(
        SystemSettingsPage page,
        string expected)
    {
        Assert.Equal(expected, SystemSettingsLauncher.GetUri(page).AbsoluteUri);
    }

    [Fact]
    public void GetUri_UnknownPage_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SystemSettingsLauncher.GetUri((SystemSettingsPage)99));
    }
}
