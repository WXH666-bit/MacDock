using System.Management;
using NLog;

namespace MacDock.Core.Services;

/// <summary>
/// 亮度控制提供者的抽象（真实实现走 WMI root\wmi），便于单测注入假实现。
/// 亮度范围 0-100；不可用（如台式机外接屏）时 IsAvailable() 返回 false。
/// </summary>
internal interface IBrightnessProvider
{
    /// <summary>当前环境是否支持亮度控制（WmiMonitorBrightness 类可查询）。</summary>
    bool IsAvailable();

    /// <summary>读取当前亮度（0-100）。失败返回 null。</summary>
    int? GetBrightness();

    /// <summary>设置亮度（0-100）。返回是否成功。</summary>
    bool SetBrightness(int level);
}

/// <summary>
/// 亮度控制服务：读写内屏亮度（WMI，覆盖 Windows 标准亮度接口，无厂商专用驱动）。
/// 供菜单栏太阳图标、浮窗滑条与滚轮步进使用。
/// 带 1 秒超时防止 WMI 挂起；任何失败都不抛异常。
/// </summary>
public sealed class BrightnessService
{
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>WMI 单次操作超时。</summary>
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(1);

    private readonly IBrightnessProvider _provider;

    public BrightnessService() : this(new WmiBrightnessProvider())
    {
    }

    /// <summary>供单测注入假提供者。</summary>
    internal BrightnessService(IBrightnessProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <summary>当前环境是否支持亮度控制（不支持的机器不显示亮度图标）。</summary>
    public bool IsAvailable
    {
        get
        {
            try
            {
                // WMI 查询可能较慢，交给后台线程并限时
                return RunWithTimeout(_provider.IsAvailable);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>读取当前亮度（0-100）。失败返回 null。</summary>
    public int? GetBrightness()
    {
        try
        {
            return RunWithTimeout(_provider.GetBrightness);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>设置亮度（0-100，超出范围会被截断）。失败返回 false。</summary>
    public bool SetBrightness(int level)
    {
        level = Math.Clamp(level, 0, 100);
        try
        {
            return RunWithTimeout(() => _provider.SetBrightness(level));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 在后台线程执行 WMI 操作，超过 1 秒则返回 default(T)（防 WMI 挂起卡 UI）。
    /// 调用方应以 provider 返回的语义解释结果（false / null 均视为失败）。
    /// </summary>
    private static T RunWithTimeout<T>(Func<T> action)
    {
        var task = Task.Run(action);
        if (!task.Wait(OperationTimeout))
        {
            Logger.Warn("亮度 WMI 操作超时，跳过本次请求");
            return default!;
        }

        return task.Result;
    }
}

/// <summary>
/// WMI 实现：WmiMonitorBrightness 读当前亮度，WmiMonitorBrightnessMethods.WmiSetBrightness 写。
/// 查询不到类（台式机/外接屏常见）时 IsAvailable() 返回 false。
/// </summary>
internal sealed class WmiBrightnessProvider : IBrightnessProvider
{
    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

    private const string NamespacePath = @"root\wmi";
    private const string InstanceQuery = "SELECT CurrentBrightness FROM WmiMonitorBrightness";
    private const string MethodsClass = "WmiMonitorBrightnessMethods";
    private const string MethodName = "WmiSetBrightness";

    private readonly object _sync = new();
    private bool? _available;

    public bool IsAvailable()
    {
        lock (_sync)
        {
            if (!_available.HasValue)
                _available = QueryAvailable();
            return _available.Value;
        }
    }

    public int? GetBrightness()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(NamespacePath, InstanceQuery);
            foreach (ManagementBaseObject obj in searcher.Get())
            {
                // 可取到 CurrentBrightness 属性即认为有效内屏
                if (obj.GetPropertyValue("CurrentBrightness") is byte brightness)
                    return brightness;
            }

            return null;
        }
        catch (Exception exception)
        {
            Logger.Warn(exception, "读取亮度失败（可能不支持亮度控制）");
            return null;
        }
    }

    public bool SetBrightness(int level)
    {
        try
        {
            // WmiSetBrightness(Brightness, Timeout)：Timeout 单位秒，1 秒即设备超时
            using var methodClass = new ManagementClass(
                $"{NamespacePath}:{MethodsClass}");
            var inParams = methodClass.GetMethodParameters(MethodName);
            inParams["Brightness"] = (uint)level;
            inParams["Timeout"] = 1u;

            var result = methodClass.InvokeMethod(MethodName, inParams, null);
            return result is not null;
        }
        catch (Exception exception)
        {
            Logger.Warn(exception, "设置亮度失败");
            return false;
        }
    }

    /// <summary>尝试建立一次查询判断类是否存在；异常/空结果视为不可用。</summary>
    private static bool QueryAvailable()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(NamespacePath, InstanceQuery);
            using var results = searcher.Get();
            return results.Count > 0;
        }
        catch
        {
            return false;
        }
    }
}
