using System.Runtime.InteropServices;
using MacDock.Core.Interop;
using NLog;

namespace MacDock.Core.Services;

/// <summary>
/// 音量端点（主音量 + 静音）的抽象，便于单测注入假实现。
/// 成功返回可空值，失败返回 null；由调用方决定降级表现。
/// </summary>
internal interface IAudioEndpoint : IDisposable
{
    /// <summary>取主音量（0.0-1.0）。失败返回 null。</summary>
    float? GetVolume();

    /// <summary>设置主音量（0.0-1.0）。返回是否成功。</summary>
    bool SetVolume(float level);

    /// <summary>查询静音状态。失败返回 null。</summary>
    bool? GetMute();

    /// <summary>设置静音。返回是否成功。</summary>
    bool SetMute(bool mute);
}

/// <summary>默认播放端点工厂的抽象（真实实现走 Core Audio COM 链路）。</summary>
internal interface IAudioEndpointFactory
{
    /// <summary>取默认播放端点；失败返回 null。</summary>
    IAudioEndpoint? GetDefaultRender();
}

/// <summary>
/// 音量控制服务：主音量读取/设置与静音（Core Audio COM，手写 interop，不加 NuGet）。
/// 供菜单栏喇叭图标、浮窗滑条与滚轮步进使用。
/// 任何失败都不抛异常——返回 false 或 null，由 UI 决定降级表现。
/// </summary>
public sealed class AudioService : IDisposable
{
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

    private readonly IAudioEndpointFactory _factory;
    private bool _disposed;

    public AudioService() : this(new Win32AudioEndpointFactory())
    {
    }

    /// <summary>供单测注入假工厂。</summary>
    internal AudioService(IAudioEndpointFactory factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>取主音量（0.0-1.0）。失败返回 null。</summary>
    public float? GetVolume()
    {
        TryDisposeGuard();
        using var endpoint = _factory.GetDefaultRender();
        return endpoint?.GetVolume();
    }

    /// <summary>设置主音量（0.0-1.0，超出范围会被截断）。失败返回 false。</summary>
    public bool SetVolume(float level)
    {
        TryDisposeGuard();
        using var endpoint = _factory.GetDefaultRender();
        return endpoint?.SetVolume(Math.Clamp(level, 0f, 1f)) ?? false;
    }

    /// <summary>查询静音状态。失败返回 null。</summary>
    public bool? GetMute()
    {
        TryDisposeGuard();
        using var endpoint = _factory.GetDefaultRender();
        return endpoint?.GetMute();
    }

    /// <summary>设置静音。失败返回 false。</summary>
    public bool SetMute(bool mute)
    {
        TryDisposeGuard();
        using var endpoint = _factory.GetDefaultRender();
        return endpoint?.SetMute(mute) ?? false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_factory is IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception exception)
            {
                Logger.Debug(exception, "释放音频服务失败");
            }
        }
    }

    private void TryDisposeGuard()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioService));
    }
}

/// <summary>Win32 后端：组装 Core Audio COM 链路，COM 对象用完即释放。</summary>
internal sealed class Win32AudioEndpointFactory : IAudioEndpointFactory, IDisposable
{
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

    private readonly IMMDeviceEnumerator _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();

    /// <summary>COM 激活请求的 IAudioEndpointVolume IID。</summary>
    private static readonly Guid IidAudioEndpointVolume =
        new("5CDF2C82-841E-4546-9722-0CF74078229A");

    /// <summary>CLSCTX_ALL：任意上下文激活（标准用法）。</summary>
    private const int ClsctxAll = 0x17;

    public IAudioEndpoint? GetDefaultRender()
    {
        IMMDevice? device = null;
        object? endpointObj = null;

        try
        {
            var hr = _enumerator.GetDefaultAudioEndpoint(
                AudioInterop.ERender, AudioInterop.EMultimedia, out device);
            if (hr != AudioInterop.S_OK || device is null)
                return null;

            var iid = IidAudioEndpointVolume;
            hr = device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out endpointObj);
            if (hr != AudioInterop.S_OK || endpointObj is null)
                return null;

            if (endpointObj is not IAudioEndpointVolume endpoint)
                return null;

            return new Win32AudioEndpoint(endpoint, device, endpointObj);
        }
        catch (Exception exception)
        {
            // COM 在无音频设备/服务未启动等场景会抛异常，静默降级
            Logger.Debug(exception, "获取默认播放端点失败");
            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            Marshal.ReleaseComObject(_enumerator);
        }
        catch (Exception exception)
        {
            Logger.Debug(exception, "释放音频枚举器失败");
        }
    }
}

/// <summary>Win32 端点实现：委托给 IAudioEndpointVolume，持有一组 COM 引用以按序释放。</summary>
internal sealed class Win32AudioEndpoint : IAudioEndpoint
{
    private readonly IAudioEndpointVolume _endpoint;
    private readonly IMMDevice _device;
    private readonly object _endpointObj;
    private bool _disposed;

    public Win32AudioEndpoint(IAudioEndpointVolume endpoint, IMMDevice device, object endpointObj)
    {
        _endpoint = endpoint;
        _device = device;
        _endpointObj = endpointObj;
    }

    public float? GetVolume()
    {
        ThrowIfDisposed();
        var hr = _endpoint.GetMasterVolumeLevelScalar(out float level);
        return hr == AudioInterop.S_OK ? level : (float?)null;
    }

    public bool SetVolume(float level)
    {
        ThrowIfDisposed();
        var context = Guid.Empty;
        var hr = _endpoint.SetMasterVolumeLevelScalar(level, ref context);
        return hr == AudioInterop.S_OK;
    }

    public bool? GetMute()
    {
        ThrowIfDisposed();
        var hr = _endpoint.GetMute(out var mute);
        return hr == AudioInterop.S_OK ? mute : (bool?)null;
    }

    public bool SetMute(bool mute)
    {
        ThrowIfDisposed();
        var context = Guid.Empty;
        var hr = _endpoint.SetMute(mute, ref context);
        return hr == AudioInterop.S_OK;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        // COM 引用按激活顺序倒序释放（endpoint 是 device.Activate 产物，先释放）
        TryRelease(_endpointObj);
        TryRelease(_device);
    }

    private static void TryRelease(object comObject)
    {
        try
        {
            Marshal.ReleaseComObject(comObject);
        }
        catch
        {
            // 已经释放过则忽略；决不让释放失败中断主流程
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(Win32AudioEndpoint));
    }
}
