using System.Runtime.InteropServices;
using MacDock.Core.Interop;
using NLog;

namespace MacDock.Core.Services;

/// <summary>
/// 音量端点变化通知源抽象（便于单测注入假实现）。由 AudioService 持有并订阅。
/// </summary>
internal interface IAudioVolumeNotifier : IDisposable
{
    /// <summary>当系统音量/静音变化时触发（可能来自 COM 原生线程，订阅方需自行封送）。</summary>
    event Action? VolumeChanged;

    /// <summary>注册通知（成功返回 true）。</summary>
    bool TryRegister();

    /// <summary>当前绑定端点的设备 ID（未注册/未绑定时为 null）。用于检测默认设备是否切换。</summary>
    string? BoundDeviceId { get; }

    /// <summary>确保绑定到当前默认播放端点：设备切换或未注册时内部重绑。成功返回 true。</summary>
    bool EnsureBoundToCurrentDefault();
}

/// <summary>
/// 音量端点变化回调的托管实现（AudioService 必须持强引用，否则 CCW 被 GC 回调崩溃）。
/// </summary>
internal sealed class ManagedVolumeCallback : IAudioEndpointVolumeCallback
{
    private readonly Action _raise;
    private bool _disposed;

    /// <param name="raise">通知回调（在 COM 原生线程调用）。</param>
    public ManagedVolumeCallback(Action raise)
    {
        _raise = raise;
    }

    /// <summary>COM 回调：解出音量为静音/音量值，交由监听方触发刷新。</summary>
    public int OnNotify(IntPtr pNotifyData)
    {
        if (_disposed || pNotifyData == IntPtr.Zero)
            return 0;

        try
        {
            // 只关心「音量/静音发生变化」这一信号，具体数值由随后的读值刷新取得，
            // 无需解析通知数据内容（也避免 P/Invoke 结构体封送的无效开销）。
            _raise();
            return 0;
        }
        catch (Exception exception)
        {
            LogManager.GetCurrentClassLogger().Debug(exception, "音量变化回调处理失败");
            return 0;
        }
    }

    /// <summary>释放标记（实际不持有 COM 引用，仅避免唤醒已关闭的委托链）。</summary>
    public void Detach()
    {
        _disposed = true;
    }
}

/// <summary>
/// Win32 通知源：持有一个长生命周期端点（IMMDevice + IAudioEndpointVolume），
/// 注册一个 IAudioEndpointVolumeCallback。用于替代 500ms 音量轮询。
/// 端点被缓存（v13 起改为缓存以接收持续通知）；Dispose 时严格注销回调 + 释放 COM。
/// </summary>
internal sealed class Win32AudioVolumeNotifier : IAudioVolumeNotifier
{
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

    private static readonly Guid IidAudioEndpointVolume =
        new("5CDF2C82-841E-4546-9722-0CF74078229A");

    private const int ClsctxAll = 0x17;

    private readonly IMMDeviceEnumerator _enumerator;
    private readonly object _sync = new();

    private IMMDevice? _device;
    private IAudioEndpointVolume? _control;
    private object? _controlObj;
    private ManagedVolumeCallback? _callback;
    private bool _registered;
    private string? _boundDeviceId;

    public event Action? VolumeChanged;

    public Win32AudioVolumeNotifier(IMMDeviceEnumerator enumerator)
    {
        _enumerator = enumerator;
    }

    public bool TryRegister()
    {
        lock (_sync)
        {
            if (_registered)
                return true;

            try
            {
                var hr = _enumerator.GetDefaultAudioEndpoint(
                    AudioInterop.ERender, AudioInterop.EMultimedia, out _device);
                if (hr != AudioInterop.S_OK || _device is null)
                    return false;

                var iid = IidAudioEndpointVolume;
                hr = _device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out _controlObj);
                if (hr != AudioInterop.S_OK || _controlObj is not IAudioEndpointVolume control)
                {
                    LogRegistrationFailure("Activate(IAudioEndpointVolume)");
                    return false;
                }

                _control = control;
                _callback = new ManagedVolumeCallback(RaiseChanged);
                hr = _control.RegisterControlChangeNotify(_callback);
                if (hr != AudioInterop.S_OK)
                {
                    LogRegistrationFailure("RegisterControlChangeNotify");
                    ReleaseResources();
                    return false;
                }

                // 记录当前绑定端点的设备 ID，供 EnsureBoundToCurrentDefault 检测默认设备切换
                _boundDeviceId = GetDeviceId(_device);
                _registered = true;
                return true;
            }
            catch (Exception exception)
            {
                Logger.Warn(exception, "注册音量变化通知失败");
                ReleaseResources();
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            UnregisterAndReleaseLocked();
        }
    }

    /// <inheritdoc />
    public string? BoundDeviceId
    {
        get
        {
            lock (_sync)
            {
                return _boundDeviceId;
            }
        }
    }

    /// <inheritdoc />
    public bool EnsureBoundToCurrentDefault()
    {
        lock (_sync)
        {
            // 取当前默认播放端点 ID；GetId 失败（无设备）视为「设备变了」，交由重绑处理
            var currentId = GetCurrentDefaultDeviceId();
            var sameDevice = !string.IsNullOrEmpty(_boundDeviceId)
                && string.Equals(_boundDeviceId, currentId, StringComparison.Ordinal);

            if (sameDevice && _registered)
                return true;

            // 设备切换（或从未注册）→ 重绑：先按 Dispose 同模式注销旧回调 + 释放旧端点，
            // 再走 TryRegister 全流程。VolumeChanged 事件挂在本 notifier 实例上，不重建实例，
            // AudioService 的订阅自然保留；Rebind 后新端点音量值由调用方读值刷新带出。
            if (_registered)
                UnregisterAndReleaseLocked();

            return TryRegister();
        }
    }

    /// <summary>在 COM 原生线程触发，交给订阅方封送。</summary>
    private void RaiseChanged() => VolumeChanged?.Invoke();

    /// <summary>
    /// 注销回调 + 释放 COM（须在 <see cref="_sync"/> 锁内调用）。Dispose 与 Rebind 共用同一套归还流程：
    /// 旧端点可能已失效，UnregisterControlChangeNotify 可能返回错误 HRESULT，try/catch 吞掉继续释放。
    /// </summary>
    private void UnregisterAndReleaseLocked()
    {
        if (_registered && _control is not null && _callback is not null)
        {
            try
            {
                _control.UnregisterControlChangeNotify(_callback);
            }
            catch (Exception exception)
            {
                Logger.Debug(exception, "注销音量变化通知失败");
            }
        }

        _callback?.Detach();
        _callback = null;
        _registered = false;
        _boundDeviceId = null;
        ReleaseResources();
    }

    /// <summary>读 IMMDevice 的设备 ID（LPWSTR 用 CoTaskMemFree 释放）；失败返回 null。</summary>
    private static string? GetDeviceId(IMMDevice device)
    {
        IntPtr idPtr = IntPtr.Zero;
        try
        {
            var hr = device.GetId(out idPtr);
            if (hr != AudioInterop.S_OK)
                return null;

            var id = Marshal.PtrToStringUni(idPtr);
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }
        catch (Exception exception)
        {
            LogManager.GetCurrentClassLogger().Debug(exception, "读取设备 ID 失败");
            return null;
        }
        finally
        {
            // GetId 返回的 LPWSTR 由 CoTaskMemFree 释放；即便读串异常也不遗漏
            if (idPtr != IntPtr.Zero)
                AudioInterop.CoTaskMemFree(idPtr);
        }
    }

    /// <summary>临时取当前默认播放端点的设备 ID（未持有端点引用，用完即释放）。失败返回 null。</summary>
    private string? GetCurrentDefaultDeviceId()
    {
        try
        {
            var hr = _enumerator.GetDefaultAudioEndpoint(
                AudioInterop.ERender, AudioInterop.EMultimedia, out var device);
            if (hr != AudioInterop.S_OK || device is null)
                return null;

            try
            {
                return GetDeviceId(device);
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        catch (Exception exception)
        {
            Logger.Debug(exception, "读取默认设备 ID 失败");
            return null;
        }
    }

    private void LogRegistrationFailure(string stage)
        => Logger.Warn("音量变化通知注册失败：{0}", stage);

    private void ReleaseResources()
    {
        if (_controlObj is not null)
        {
            try
            {
                Marshal.ReleaseComObject(_controlObj);
            }
            catch
            {
            }

            _controlObj = null;
        }

        if (_device is not null)
        {
            try
            {
                Marshal.ReleaseComObject(_device);
            }
            catch
            {
            }

            _device = null;
        }

        _control = null;
    }
}
