using System.Windows;
using System.Windows.Threading;
using MacDock.Core.Models;
using MacDock.Core.Services;
using Microsoft.Win32;
using NLog;

namespace MacDock.UI.Services;

/// <summary>
/// 统一管理运行时主题资源。跟随系统时只读当前用户的应用主题偏好，
/// 不修改 Windows 注册表或系统主题。
/// </summary>
public sealed class ThemeManager : IDisposable
{
    private const string PersonalizeRegistryPath =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";

    private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();

    private readonly ThemeSettingsStore _store;
    private readonly Dispatcher _dispatcher;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private bool _disposed;

    public ThemeManager(
        ThemeSettingsStore store,
        ThemeSettings initialSettings,
        Dispatcher dispatcher)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(initialSettings);
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        Mode = initialSettings.Mode;
        ApplyTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>当前持久化的主题模式。</summary>
    public AppThemeMode Mode { get; private set; }

    /// <summary>当前实际应用的是深色资源。</summary>
    public bool IsDark { get; private set; }

    /// <summary>主题模式或系统跟随结果发生变化时触发。</summary>
    public event EventHandler? ThemeChanged;

    /// <summary>原子保存模式，并在 UI Dispatcher 上切换资源。</summary>
    public async Task SetModeAsync(
        AppThemeMode mode,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Mode == mode)
                return;

            cancellationToken.ThrowIfCancellationRequested();
            var settings = new ThemeSettings { Mode = mode };

            // 原子写一旦开始就让它完成；写成功后也必须同步更新内存主题，
            // 避免取消竞态造成磁盘与当前会话不一致。
            await Task.Run(() => _store.Save(settings)).ConfigureAwait(false);

            await _dispatcher.InvokeAsync(
                () =>
                {
                    if (_disposed)
                        return;

                    Mode = mode;
                    ApplyTheme();
                },
                DispatcherPriority.Normal);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_disposed || Mode != AppThemeMode.System)
            return;

        try
        {
            _dispatcher.BeginInvoke(ApplyTheme, DispatcherPriority.Background);
        }
        catch (InvalidOperationException)
        {
            // Dispatcher 正在退出，OnExit 会释放订阅。
        }
    }

    private void ApplyTheme()
    {
        if (_disposed)
            return;

        var useDark = Mode switch
        {
            AppThemeMode.Dark => true,
            AppThemeMode.Light => false,
            _ => !SystemUsesLightTheme(),
        };

        var application = Application.Current;
        if (application is null)
            return;

        var dictionaries = application.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(IsThemeDictionary);
        var source = new Uri(
            useDark ? "Themes/DarkTheme.xaml" : "Themes/LightTheme.xaml",
            UriKind.Relative);

        if (existing is null)
        {
            dictionaries.Insert(0, new ResourceDictionary { Source = source });
        }
        else if (!string.Equals(
                     existing.Source?.OriginalString,
                     source.OriginalString,
                     StringComparison.OrdinalIgnoreCase))
        {
            existing.Source = source;
        }

        IsDark = useDark;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsThemeDictionary(ResourceDictionary dictionary)
    {
        var source = dictionary.Source?.OriginalString;
        return source is not null
            && (source.EndsWith("Themes/LightTheme.xaml", StringComparison.OrdinalIgnoreCase)
                || source.EndsWith("Themes/DarkTheme.xaml", StringComparison.OrdinalIgnoreCase));
    }

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeRegistryPath);
            return key?.GetValue(AppsUseLightThemeValue) switch
            {
                int value => value != 0,
                _ => true,
            };
        }
        catch (Exception exception) when (
            exception is System.Security.SecurityException
            or UnauthorizedAccessException
            or IOException)
        {
            Logger.Debug(exception, "读取 Windows 应用主题失败，回退浅色主题");
            return true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }
}
