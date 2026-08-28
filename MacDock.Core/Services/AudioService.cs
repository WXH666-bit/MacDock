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
internal interface IAudioEndpointFactory : IDisposable
{
    /// <summary>取默认播放端点；失败返回 null。</summary>
    IAudioEndpoint? GetDefaultRender();

    /// <summary>创建音量端点变化通知源（替代 500ms 轮询）。</summary>
    IAudioVolumeNotifier CreateNotificationSource();
}

/// <summary>
/// 音量控制服务：主音量读取/设置与静音（Core Audio COM，手写 interop，不加 NuGet）。
/// 供菜单栏喇叭图标、浮窗滑条与滚轮步进使用。
/// 任何失败都不抛异常——返回 false 或 null，由 UI 决定降级表现。
/// 同时持有长驻音量端点变化回调，替代 500ms 轮询：<see cref="VolumeChanged"/> 在系统音量变化时触发。
/// </summary>
public sealed class AudioService : IDisposable
{
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

    private readonly Func<IAudioEndpointFactory> _factoryFactory;
    private readonly object _sync = new();
    private IAudioEndpointFactory? _factory;
    private IAudioVolumeNotifier? _notifier;
    private long _nextInitializationAttemptTick;
    private bool _disposed;

    /// <summary>系统音量/静音变化时触发（可能在 COM 原生线程，订阅方自行封送）。</summary>
    public event Action? VolumeChanged;

    public AudioService()
        : this(
            static () => new Win32AudioEndpointFactory(),
            initializeImmediately: false)
    {
    }

    /// <summary>供单测注入假工厂。</summary>
    internal AudioService(IAudioEndpointFactory factory)
        : this(
            CreateFactoryAccessor(factory),
            initializeImmediately: true)
    {
    }

    /// <summary>供单测验证默认构造使用的惰性初始化边界。</summary>
    internal AudioService(Func<IAudioEndpointFactory> factoryFactory)
        : this(factoryFactory, initializeImmediately: false)
    {
    }

    private AudioService(
        Func<IAudioEndpointFactory> factoryFactory,
        bool initializeImmediately)
    {
        _factoryFactory = factoryFactory
            ?? throw new ArgumentNullException(nameof(factoryFactory));

        if (initializeImmediately)
        {
            lock (_sync)
                EnsureInitializedNoLock();
        }
    }

    private void OnNotifierChanged() => VolumeChanged?.Invoke();

    /// <summary>是否存在可用播放设备（无音频设备时为 false，UI 据此隐藏音量图标）。</summary>
    public bool IsAvailable
    {
        get
        {
            lock (_sync)
            {
                ThrowIfDisposedNoLock();
                if (!EnsureInitializedNoLock())
                    return false;

                using var endpoint = _factory!.GetDefaultRender();
                return endpoint is not null;
            }
        }
    }

    /// <summary>取主音量（0.0-1.0）。失败返回 null。</summary>
    public float? GetVolume()
    {
        lock (_sync)
        {
            ThrowIfDisposedNoLock();
            if (!EnsureInitializedNoLock())
                return null;

            using var endpoint = _factory!.GetDefaultRender();
            return endpoint?.GetVolume();
        }
    }

    /// <summary>设置主音量（0.0-1.0，超出范围会被截断）。失败返回 false。</summary>
    public bool SetVolume(float level)
    {
        lock (_sync)
        {
            ThrowIfDisposedNoLock();
            if (!EnsureInitializedNoLock())
                return false;

            using var endpoint = _factory!.GetDefaultRender();
            return endpoint?.SetVolume(Math.Clamp(level, 0f, 1f)) ?? false;
        }
    }

    /// <summary>查询静音状态。失败返回 null。</summary>
    public bool? GetMute()
    {
        lock (_sync)
        {
            ThrowIfDisposedNoLock();
            if (!EnsureInitializedNoLock())
                return null;

            using var endpoint = _factory!.GetDefaultRender();
            return endpoint?.GetMute();
        }
    }

    /// <summary>设置静音。失败返回 false。</summary>
    public bool SetMute(bool mute)
    {
        lock (_sync)
        {
            ThrowIfDisposedNoLock();
            if (!EnsureInitializedNoLock())
                return false;

            using var endpoint = _factory!.GetDefaultRender();
            return endpoint?.SetMute(mute) ?? false;
        }
    }

    /// <summary>
    /// 确保音量通知源绑定到当前默认播放端点（拔插耳机/切换默认输出后，通知源可能仍挂旧设备）。
    /// 由实现内部判定设备是否变化并重绑；任何失败都不抛异常（内部静默），供调用方周期触发自愈。
    /// </summary>
    public void EnsureVolumeNotifierHealthy()
    {
        try
        {
            lock (_sync)
            {
                if (_disposed || !EnsureInitializedNoLock())
                    return;

                _notifier!.EnsureBoundToCurrentDefault();
            }
        }
        catch (Exception exception)
        {
            Logger.Debug(exception, "音量通知源健康检查失败");
        }
    }

    public void Dispose()
    {
        IAudioVolumeNotifier? notifier;
        IAudioEndpointFactory? factory;
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            notifier = _notifier;
            factory = _factory;
            _notifier = null;
            _factory = null;
        }

        if (notifier is not null)
            notifier.VolumeChanged -= OnNotifierChanged;
        try
        {
            notifier?.Dispose();
        }
        catch (Exception exception)
        {
            Logger.Debug(exception, "释放音量通知源失败");
        }

        try
        {
            factory?.Dispose();
        }
        catch (Exception exception)
        {
            Logger.Debug(exception, "释放音频服务失败");
        }
    }

    private bool EnsureInitializedNoLock()
    {
        if (_factory is not null && _notifier is not null)
            return true;

        if (Environment.TickCount64 < _nextInitializationAttemptTick)
            return false;

        IAudioEndpointFactory? factory = null;
        IAudioVolumeNotifier? notifier = null;
        try
        {
            factory = _factoryFactory();
            notifier = factory.CreateNotificationSource();
            notifier.VolumeChanged += OnNotifierChanged;
            notifier.TryRegister();
            _factory = factory;
            _notifier = notifier;
            _nextInitializationAttemptTick = 0;
            return true;
        }
        catch (Exception exception)
        {
            if (notifier is not null)
                notifier.VolumeChanged -= OnNotifierChanged;
            try
            {
                notifier?.Dispose();
                factory?.Dispose();
            }
            catch (Exception disposeException)
            {
                Logger.Debug(disposeException, "清理失败的音频初始化资源失败");
            }

            Logger.Debug(exception, "初始化音频服务失败");
            _nextInitializationAttemptTick = Environment.TickCount64 + 5000;
            return false;
        }
    }

    private void ThrowIfDisposedNoLock()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioService));
    }

    private static Func<IAudioEndpointFactory> CreateFactoryAccessor(
        IAudioEndpointFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return () => factory;
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

    /// <summary>创建音量端点变化通知源（共享同一个设备枚举器）。</summary>
    public IAudioVolumeNotifier CreateNotificationSource() => new Win32AudioVolumeNotifier(_enumerator);

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
