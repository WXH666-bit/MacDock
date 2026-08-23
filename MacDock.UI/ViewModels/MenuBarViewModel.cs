using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using MacDock.Core.Services;

namespace MacDock.UI.ViewModels;

/// <summary>
/// 顶部菜单栏视图模型：左侧前台应用名、右侧时间。
/// 前台应用数据源复用 MainViewModel 持有的 WindowMonitor（不重复挂钩）。
/// </summary>
public sealed partial class MenuBarViewModel : ObservableObject, IDisposable
{
    /// <summary>无前台应用（如刚启动、桌面）时显示的兜底名。</summary>
    private const string FallbackAppName = "MacDock";

    private readonly WindowMonitor _windowMonitor;
    private readonly DispatcherTimer _clockTimer;
    private bool _disposed;

    /// <summary>当前前台应用显示名（窗口标题优先，退化为进程名）。</summary>
    [ObservableProperty]
    private string _foregroundAppName = FallbackAppName;

    /// <summary>当前时间文本，格式「周X M月d日 HH:mm」。</summary>
    [ObservableProperty]
    private string _clockText = string.Empty;

    /// <param name="windowMonitor">共享的窗口监控实例，生命周期由调用方持有。</param>
    public MenuBarViewModel(WindowMonitor windowMonitor)
    {
        _windowMonitor = windowMonitor ?? throw new ArgumentNullException(nameof(windowMonitor));

        // 启动时取一次当前前台应用，避免等到第一次切换才有内容
        var current = _windowMonitor.GetForegroundApp();
        if (current is not null)
            ForegroundAppName = FormatAppName(current.Value.ProcessName, current.Value.WindowTitle);

        _windowMonitor.ForegroundAppChanged += OnForegroundAppChanged;

        UpdateClock();
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _clockTimer.Tick += OnClockTick;
        _clockTimer.Start();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _windowMonitor.ForegroundAppChanged -= OnForegroundAppChanged;
        _clockTimer.Tick -= OnClockTick;
        _clockTimer.Stop();
    }

    /// <summary>WindowMonitor 回调在原生线程，须回 UI 线程更新绑定属性。</summary>
    private void OnForegroundAppChanged(string processName, string? windowTitle)
    {
        if (_disposed)
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        dispatcher.BeginInvoke(
            () =>
            {
                if (!_disposed)
                    ForegroundAppName = FormatAppName(processName, windowTitle);
            },
            DispatcherPriority.Background);
    }

    /// <summary>
    /// 显示名优先级：主窗口标题 > 进程名。
    /// M2.1 不做进程名→中文友好名映射（留 M2.2）。
    /// </summary>
    private static string FormatAppName(string processName, string? windowTitle)
    {
        if (!string.IsNullOrWhiteSpace(windowTitle))
            return windowTitle.Trim();

        return string.IsNullOrWhiteSpace(processName) ? FallbackAppName : processName;
    }

    private void OnClockTick(object? sender, EventArgs e) => UpdateClock();

    private void UpdateClock() => ClockText = FormatClock(DateTime.Now, CultureInfo.CurrentCulture);

    /// <summary>
    /// 生成时间文本，格式「周X M月d日 HH:mm」（如「周日 8月23日 16:38」）。
    /// 非中文区域改用该区域自带的月日格式，避免硬编码中文的「月/日」。
    /// </summary>
    internal static string FormatClock(DateTime now, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        // "ddd" 在中文区域给出「周日」，英文区域给出「Sun」，各自符合习惯
        var weekday = now.ToString("ddd", culture);
        var date = culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? now.ToString(@"M月d日", culture)
            : now.ToString(culture.DateTimeFormat.MonthDayPattern, culture);
        var time = now.ToString("HH:mm", culture);

        return $"{weekday} {date} {time}";
    }
}
