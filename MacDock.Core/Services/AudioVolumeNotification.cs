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
            // 只关心 bMuted / fMasterVolume；声道音量忽略
            Marshal.PtrToStructure<AudioVolumeNotificationData>(pNotifyData);
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
            ReleaseResources();
        }
    }

    /// <summary>在 COM 原生线程触发，交给订阅方封送。</summary>
    private void RaiseChanged() => VolumeChanged?.Invoke();

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
