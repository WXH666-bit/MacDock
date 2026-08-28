using MacDock.Core.Services;
using Xunit;

namespace MacDock.Tests;

/// <summary>
/// AudioService 逻辑单测：用假端点工厂验证读写/静音/失败降级/边界截断，
/// 不触碰真实 Core Audio COM（测试环境无音频设备，真实调用只会失败返回 null）。
/// </summary>
public sealed class AudioServiceTests
{
    private sealed class FakeEndpoint : IAudioEndpoint
    {
        public float? Volume { get; set; } = 0.5f;

        public bool? Mute { get; set; } = false;

        public bool FailAll { get; set; }

        public bool Disposed { get; private set; }

        public float? GetVolume() => FailAll ? null : Volume;

        public bool SetVolume(float level)
        {
            if (FailAll)
                return false;

            Volume = level;
            return true;
        }

        public bool? GetMute() => FailAll ? null : Mute;

        public bool SetMute(bool mute)
        {
            if (FailAll)
                return false;

            Mute = mute;
            return true;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeNotifier : IAudioVolumeNotifier
    {
        public bool RegisterResult { get; set; } = true;

        public bool RegisterCalled { get; private set; }

        public bool Disposed { get; private set; }

        public string? BoundDeviceId { get; set; }

        public bool EnsureResult { get; set; } = true;

        public int EnsureCalls { get; private set; }

        public event Action? VolumeChanged;

        public bool TryRegister()
        {
            RegisterCalled = true;
            return RegisterResult;
        }

        public bool EnsureBoundToCurrentDefault()
        {
            EnsureCalls++;
            return EnsureResult;
        }

        public void Raise() => VolumeChanged?.Invoke();

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeFactory : IAudioEndpointFactory
    {
        public FakeEndpoint? Endpoint { get; set; } = new FakeEndpoint();

        public FakeNotifier Notifier { get; } = new();

        public int GetCount { get; private set; }

        public IAudioEndpoint? GetDefaultRender()
        {
            GetCount++;
            return Endpoint;
        }

        public IAudioVolumeNotifier CreateNotificationSource() => Notifier;

        public void Dispose()
        {
        }
    }

    [Fact]
    public void GetVolume_ReturnsEndpointVolume()
    {
        using var service = new AudioService(new FakeFactory { Endpoint = new FakeEndpoint { Volume = 0.75f } });

        Assert.Equal(0.75f, service.GetVolume());
    }

    [Fact]
    public void SetVolume_ClampsToRange()
    {
        var endpoint = new FakeEndpoint { Volume = 0.5f };
        using var service = new AudioService(new FakeFactory { Endpoint = endpoint });

        service.SetVolume(1.8f);
        Assert.Equal(1f, endpoint.Volume);

        service.SetVolume(-0.2f);
        Assert.Equal(0f, endpoint.Volume);
    }

    [Fact]
    public void SetVolume_RoundTrips()
    {
        var endpoint = new FakeEndpoint();
        using var service = new AudioService(new FakeFactory { Endpoint = endpoint });

        Assert.True(service.SetVolume(0.42f));
        Assert.Equal(0.42f, endpoint.Volume);
        Assert.Equal(0.42f, service.GetVolume());
    }

    [Fact]
    public void GetMute_ReturnsEndpointMute()
    {
        using var service = new AudioService(new FakeFactory { Endpoint = new FakeEndpoint { Mute = true } });

        Assert.True(service.GetMute());
    }

    [Fact]
    public void SetMute_Toggles()
    {
        var endpoint = new FakeEndpoint();
        using var service = new AudioService(new FakeFactory { Endpoint = endpoint });

        Assert.True(service.SetMute(true));
        Assert.True(endpoint.Mute);
        Assert.True(service.GetMute());
    }

    [Fact]
    public void NoEndpoint_ReturnsNullAndFalse()
    {
        using var service = new AudioService(new FakeFactory { Endpoint = null });

        Assert.Null(service.GetVolume());
        Assert.Null(service.GetMute());
        Assert.False(service.SetVolume(0.5f));
        Assert.False(service.SetMute(true));
    }

    [Fact]
    public void EndpointFailure_ReturnsNullAndFalseWithoutThrowing()
    {
        using var service = new AudioService(new FakeFactory { Endpoint = new FakeEndpoint { FailAll = true } });

        Assert.Null(service.GetVolume());
        Assert.Null(service.GetMute());
        Assert.False(service.SetVolume(0.5f));
        Assert.False(service.SetMute(true));
    }

    [Fact]
    public void EveryOperation_ReleasesTheEndpoint()
    {
        var endpoint = new FakeEndpoint();
        var factory = new FakeFactory { Endpoint = endpoint };
        using var service = new AudioService(factory);

        Assert.False(endpoint.Disposed);
        service.GetVolume();
        Assert.True(endpoint.Disposed);
        Assert.Equal(1, factory.GetCount);
    }

    [Fact]
    public void Ctor_RegistersNotificationSource()
    {
        var factory = new FakeFactory();

        using var service = new AudioService(factory);

        Assert.True(factory.Notifier.RegisterCalled);
    }

    [Fact]
    public void LazyFactory_IsNotCreatedUntilTheFirstAudioOperation()
    {
        var factory = new FakeFactory();
        var createCalls = 0;
        using var service = new AudioService(() =>
        {
            createCalls++;
            return factory;
        });

        Assert.Equal(0, createCalls);

        Assert.Equal(0.5f, service.GetVolume());
        Assert.Equal(1, createCalls);
        Assert.True(factory.Notifier.RegisterCalled);
    }

    [Fact]
    public void NotifierVolumeChange_RaisesServiceVolumeChanged()
    {
        var factory = new FakeFactory();
        var raised = 0;
        using var service = new AudioService(factory);
        service.VolumeChanged += () => raised++;

        factory.Notifier.Raise();

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Dispose_UnsubscribesAndDisposesNotifier()
    {
        var factory = new FakeFactory();
        var service = new AudioService(factory);
        service.VolumeChanged += () => { };

        service.Dispose();

        Assert.True(factory.Notifier.Disposed);
        // Dispose 后不再触发
        factory.Notifier.Raise();
    }

    // 设备切换（设备 ID 变化 → 重绑）与未注册状态的检测逻辑位于 Win32AudioVolumeNotifier，
    // 需真实 Core Audio COM 才能驱动，单元测试无法替代——以下两例验证 AudioService 的透传契约，
    // 实际重绑结果由真机验收（拔插耳机/切默认输出后回调恢复）确认。

    [Fact]
    public void EnsureVolumeNotifierHealthy_DelegatesToNotifier()
    {
        var factory = new FakeFactory();
        using var service = new AudioService(factory);

        service.EnsureVolumeNotifierHealthy();

        Assert.Equal(1, factory.Notifier.EnsureCalls);
    }

    [Fact]
    public void EnsureVolumeNotifierHealthy_AfterDispose_DoesNotTouchNotifier()
    {
        var factory = new FakeFactory();
        var service = new AudioService(factory);
        service.Dispose();

        service.EnsureVolumeNotifierHealthy();

        Assert.Equal(0, factory.Notifier.EnsureCalls);
    }
}
