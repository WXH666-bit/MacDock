using System.Runtime.InteropServices;

namespace MacDock.Core.Interop;

/// <summary>
/// Core Audio COM 互操作声明（手写，不加 NuGet 包）。
/// 仅覆盖主音量控制链路：MMDeviceEnumerator → IMMDevice → IAudioEndpointVolume。
/// 接口方法必须按真实 vtable 顺序声明，未用到的方法以占位声明保持槽位正确。
/// </summary>
internal static class AudioInterop
{
    /// <summary>eRender：渲染（播放）设备。</summary>
    public const int ERender = 0;

    /// <summary>eMultimedia：多媒体用途（默认音量策略）。</summary>
    public const int EMultimedia = 1;

    /// <summary>S_OK。</summary>
    public const int S_OK = 0;

    /// <summary>CoTaskMemFree：释放 COM 任务分配器分配的内存（如 GetId 返回的 LPWSTR 设备 ID）。</summary>
    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr ptr);
}

/// <summary>
/// IMMDeviceEnumerator：音频设备枚举器（MMDevice API 入口）。
/// https://learn.microsoft.com/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immdeviceenumerator
/// </summary>
[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    /// <summary>枚举音频端点设备（未用，vtable 槽位占位）。</summary>
    [PreserveSig]
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

    /// <summary>取默认音频端点设备。</summary>
    [PreserveSig]
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);

    /// <summary>按设备 ID 取设备（未用，槽位占位）。</summary>
    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

    /// <summary>注册设备变化回调（未用，槽位占位）。</summary>
    [PreserveSig]
    int RegisterEndpointNotificationCallback(IntPtr client);

    /// <summary>注销设备变化回调（未用，槽位占位）。</summary>
    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IntPtr client);
}

/// <summary>
/// IMMDevice：单个音频端点设备。
/// https://learn.microsoft.com/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immdevice
/// </summary>
[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    /// <summary>激活设备上的功能接口（如 IAudioEndpointVolume）。</summary>
    [PreserveSig]
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object iface);

    /// <summary>
    /// 打开设备属性存储。本实现不调用，仅作 vtable 槽位占位——IMMDevice 的真实 vtable 顺序为
    /// IUnknown(槽0-2) → Activate(槽3) → OpenPropertyStore(槽4) → GetId(槽5) → GetState(槽6)。
    /// 若跳过此占位直接声明 GetId，会因槽位错位而调到错误方法（P2-2 命门）。
    /// </summary>
    [PreserveSig]
    int OpenPropertyStore(int stgmAccess, [MarshalAs(UnmanagedType.IUnknown)] out object properties);

    /// <summary>取设备稳定的唯一 ID（LPWSTR，须用 CoTaskMemFree 释放）。用于判断默认设备是否切换。</summary>
    [PreserveSig]
    int GetId(out IntPtr ppstrId);

    /// <summary>取设备状态（未用，槽位占位）。</summary>
    [PreserveSig]
    int GetState(out int state);
}

/// <summary>
/// IAudioEndpointVolume：端点主音量控制（0.0-1.0 标量值 + 静音）。
/// https://learn.microsoft.com/windows/win32/api/endpointvolume/nn-endpointvolume-iaudioendpointvolume
/// </summary>
[ComImport]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    /// <summary>注册音量变化回调（ReceiveControlChangeNotifications）。</summary>
    [PreserveSig]
    int RegisterControlChangeNotify(IAudioEndpointVolumeCallback client);

    /// <summary>注销音量变化回调。</summary>
    [PreserveSig]
    int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback client);

    /// <summary>取声道数（槽位占位）。</summary>
    [PreserveSig]
    int GetChannelCount(out uint channelCount);

    /// <summary>按分贝设置主音量。</summary>
    [PreserveSig]
    int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);

    /// <summary>按分贝设置主音量（槽位占位）。</summary>
    [PreserveSig]
    int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

    /// <summary>取主音量（分贝，槽位占位）。</summary>
    [PreserveSig]
    int GetMasterVolumeLevel(out float levelDb);

    /// <summary>取主音量（0.0-1.0 标量）。</summary>
    [PreserveSig]
    int GetMasterVolumeLevelScalar(out float level);

    /// <summary>按声道设置音量（槽位占位）。</summary>
    [PreserveSig]
    int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);

    /// <summary>按声道设置音量标量（槽位占位）。</summary>
    [PreserveSig]
    int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);

    /// <summary>取声道音量（分贝，槽位占位）。</summary>
    [PreserveSig]
    int GetChannelVolumeLevel(uint channel, out float levelDb);

    /// <summary>取声道音量标量（槽位占位）。</summary>
    [PreserveSig]
    int GetChannelVolumeLevelScalar(uint channel, out float level);

    /// <summary>设置静音。</summary>
    [PreserveSig]
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);

    /// <summary>查询静音状态。</summary>
    [PreserveSig]
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

/// <summary>
/// IAudioEndpointVolumeCallback：音量端点变化通知。
/// 接口 IUnknown 之后仅一个方法 OnNotify(PAUDIO_VOLUME_NOTIFICATION_DATA*)。
/// </summary>
[ComImport]
[Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolumeCallback
{
    /// <summary>
    /// 音量/静音变化回调（COM 原生线程调用）。参数为 PAUDIO_VOLUME_NOTIFICATION_DATA 数组指针
    /// （该接口每回调传入长度为 1 的数组）。bMuted 为 1 表示静音，fMasterVolume 为 0.0-1.0。
    /// </summary>
    [PreserveSig]
    int OnNotify(IntPtr pNotifyData);
}

/// <summary>
/// PAUDIO_VOLUME_NOTIFICATION_DATA 的结构（AudioVolumeNotificationData）前导字段。
/// 后续为 nChannels 个声道音量。实现并不解析该结构——音量/静音具体值由通知后的读值刷新取得，
/// 保留此类型仅作数据布局的文档价值（说明回调内容：GUID/静音/主音量/声道数）。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AudioVolumeNotificationData
{
    public Guid guidEventContext;
    public int bMuted;        // BOOL
    public float fMasterVolume;   // 0.0-1.0
    public uint nChannels;
}

/// <summary>
/// MMDeviceEnumerator COM 类（CLSID_BCDE0395-E52F-467C-8E3D-C4579291692E）。
/// </summary>
[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject
{
}
