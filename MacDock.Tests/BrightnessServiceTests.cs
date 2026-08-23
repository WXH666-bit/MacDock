using MacDock.Core.Services;
using Xunit;

namespace MacDock.Tests;

/// <summary>
/// BrightnessService 逻辑单测：用假提供者验证可用性/读写/边界截断，
/// 不触碰真实 WMI（测试环境通常无亮度类，真实调用只会返回不可用）。</summary>
public sealed class BrightnessServiceTests
{
    private sealed class FakeProvider : IBrightnessProvider
    {
        public bool Available { get; set; } = true;

        public int? Brightness { get; set; } = 50;

        public int SetRequests { get; private set; }

        public bool IsAvailable() => Available;

        public int? GetBrightness() => Brightness;

        public bool SetBrightness(int level)
        {
            SetRequests++;
            Brightness = level;
            return true;
        }
    }

    [Fact]
    public void IsAvailable_ReflectsProvider()
    {
        var available = new BrightnessService(new FakeProvider { Available = true });
        Assert.True(available.IsAvailable);

        var unavailable = new BrightnessService(new FakeProvider { Available = false });
        Assert.False(unavailable.IsAvailable);
    }

    [Fact]
    public void GetBrightness_ReturnsProviderValue()
    {
        var service = new BrightnessService(new FakeProvider { Brightness = 73 });
        Assert.Equal(73, service.GetBrightness());
    }

    [Fact]
    public void SetBrightness_ClampsToRange()
    {
        var provider = new FakeProvider();
        var service = new BrightnessService(provider);

        service.SetBrightness(120);
        Assert.Equal(100, provider.Brightness);

        service.SetBrightness(-5);
        Assert.Equal(0, provider.Brightness);
    }

    [Fact]
    public void SetBrightness_RoundTrips()
    {
        var provider = new FakeProvider();
        var service = new BrightnessService(provider);

        Assert.True(service.SetBrightness(65));
        Assert.Equal(65, provider.Brightness);
        Assert.Equal(65, service.GetBrightness());
    }

    [Fact]
    public void Unavailable_GetReturnsNull()
    {
        var service = new BrightnessService(new FakeProvider { Brightness = null });
        Assert.Null(service.GetBrightness());
    }
}
