using MacDock.Core.Services;
using Xunit;

namespace MacDock.Tests;

public sealed class WindowSnapshotServiceTests
{
    [Theory]
    [InlineData(31, 100, false)]
    [InlineData(100, 31, false)]
    [InlineData(32, 32, true)]
    [InlineData(1920, 1080, true)]
    [InlineData(3840, 2160, true)]
    [InlineData(5120, 2880, false)]
    [InlineData(9000, 100, false)]
    [InlineData(int.MaxValue, int.MaxValue, false)]
    public void CaptureSizePolicy_IsBounded(int width, int height, bool expected)
    {
        Assert.Equal(
            expected,
            WindowSnapshotService.IsCaptureSizeAllowed(width, height));
    }

    [Theory]
    [InlineData(800, 600, 800, 600)]
    [InlineData(1920, 1080, 1600, 900)]
    [InlineData(3840, 2160, 1600, 900)]
    [InlineData(2160, 3840, 900, 1600)]
    [InlineData(1600, 1600, 1265, 1265)]
    [InlineData(31, 100, 0, 0)]
    public void BitmapSizePolicy_DownscalesLargeWindows(
        int width,
        int height,
        int expectedWidth,
        int expectedHeight)
    {
        Assert.Equal(
            (expectedWidth, expectedHeight),
            WindowSnapshotService.GetBitmapSize(width, height));
    }
}
