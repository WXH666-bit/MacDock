using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MacDock.Core.Models;
using MacDock.Core.Services;
using MacDock.UI.Services;

namespace MacDock.UI.ViewModels;

/// <summary>控制中心视图模型：复用菜单栏的音量／亮度状态，并管理主题入口。</summary>
public sealed class ControlCenterViewModel : ObservableObject, IDisposable
{
    private readonly MenuBarViewModel _menuBar;
    private readonly ThemeManager _themeManager;
    private bool _isRefreshing;
    private bool _disposed;
    private double _volume;
    private double _brightness;
    private bool _isAudioAvailable;
    private bool _isBrightnessAvailable;
    private bool _isThemeBusy;
    private string? _error;

    public ControlCenterViewModel(
        MenuBarViewModel menuBar,
        ThemeManager themeManager)
    {
        _menuBar = menuBar ?? throw new ArgumentNullException(nameof(menuBar));
        _themeManager = themeManager ?? throw new ArgumentNullException(nameof(themeManager));

        OpenWifiSettingsCommand = new RelayCommand(
            () => OpenSettings(SystemSettingsPage.Wifi));
        OpenBluetoothSettingsCommand = new RelayCommand(
            () => OpenSettings(SystemSettingsPage.Bluetooth));
        OpenFocusAssistSettingsCommand = new RelayCommand(
            () => OpenSettings(SystemSettingsPage.FocusAssist));
        ToggleMuteCommand = new RelayCommand(_menuBar.ToggleMuteFromFlyout);
        UseSystemThemeCommand = new AsyncRelayCommand(
            cancellationToken => SetThemeAsync(AppThemeMode.System, cancellationToken));
        UseLightThemeCommand = new AsyncRelayCommand(
            cancellationToken => SetThemeAsync(AppThemeMode.Light, cancellationToken));
        UseDarkThemeCommand = new AsyncRelayCommand(
            cancellationToken => SetThemeAsync(AppThemeMode.Dark, cancellationToken));

        _menuBar.ControlsRefreshed += RefreshControls;
        _themeManager.ThemeChanged += OnThemeChanged;
        RefreshControls();
        NotifyThemeState();
    }

    public double Volume
    {
        get => _volume;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (!SetProperty(ref _volume, clamped) || _isRefreshing || !IsAudioAvailable)
                return;

            _menuBar.SetVolumeFromFlyout(clamped);
        }
    }

    public double Brightness
    {
        get => _brightness;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (!SetProperty(ref _brightness, clamped)
                || _isRefreshing
                || !IsBrightnessAvailable)
            {
                return;
            }

            _menuBar.SetBrightnessFromFlyout(clamped);
        }
    }

    public bool IsAudioAvailable
    {
        get => _isAudioAvailable;
        private set => SetProperty(ref _isAudioAvailable, value);
    }

    public bool IsBrightnessAvailable
    {
        get => _isBrightnessAvailable;
        private set => SetProperty(ref _isBrightnessAvailable, value);
    }

    public bool IsMuted => _menuBar.IsMuted;

    public bool IsThemeBusy
    {
        get => _isThemeBusy;
        private set
        {
            if (!SetProperty(ref _isThemeBusy, value))
                return;

            OnPropertyChanged(nameof(CanChangeTheme));
        }
    }

    public bool CanChangeTheme => !IsThemeBusy;

    public bool IsSystemTheme => _themeManager.Mode == AppThemeMode.System;

    public bool IsLightTheme => _themeManager.Mode == AppThemeMode.Light;

    public bool IsDarkTheme => _themeManager.Mode == AppThemeMode.Dark;

    public string EffectiveThemeLabel => _themeManager.IsDark ? "当前为深色" : "当前为浅色";

    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    public IRelayCommand OpenWifiSettingsCommand { get; }

    public IRelayCommand OpenBluetoothSettingsCommand { get; }

    public IRelayCommand OpenFocusAssistSettingsCommand { get; }

    public IRelayCommand ToggleMuteCommand { get; }

    public IAsyncRelayCommand UseSystemThemeCommand { get; }

    public IAsyncRelayCommand UseLightThemeCommand { get; }

    public IAsyncRelayCommand UseDarkThemeCommand { get; }

    /// <summary>滑块松开时立即提交亮度队列中的最新值。</summary>
    public void FlushBrightnessWrite() => _menuBar.FlushBrightnessWrite();

    private void RefreshControls()
    {
        if (_disposed)
            return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(RefreshControls);
            return;
        }

        _isRefreshing = true;
        try
        {
            IsAudioAvailable = _menuBar.IsAudioAvailable;
            IsBrightnessAvailable = _menuBar.IsBrightnessAvailable;
            Volume = _menuBar.GetVolumeLevel() ?? 0;
            Brightness = _menuBar.CachedBrightnessLevel ?? 0;
            OnPropertyChanged(nameof(IsMuted));
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private async Task SetThemeAsync(
        AppThemeMode mode,
        CancellationToken cancellationToken)
    {
        if (IsThemeBusy)
            return;

        IsThemeBusy = true;
        Error = null;
        try
        {
            await _themeManager.SetModeAsync(mode, cancellationToken);
            NotifyThemeState();
        }
        catch (OperationCanceledException)
        {
            Error = "主题切换已取消。";
            NotifyThemeState();
        }
        catch (Exception exception)
        {
            Error = $"主题设置无法保存：{exception.Message}";
            NotifyThemeState();
        }
        finally
        {
            IsThemeBusy = false;
        }
    }

    private void OpenSettings(SystemSettingsPage page)
    {
        Error = null;
        try
        {
            SystemSettingsLauncher.Open(page);
        }
        catch (Exception exception)
        {
            Error = $"无法打开 Windows 设置：{exception.Message}";
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => NotifyThemeState();

    private void NotifyThemeState()
    {
        OnPropertyChanged(nameof(IsSystemTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
        OnPropertyChanged(nameof(EffectiveThemeLabel));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _menuBar.ControlsRefreshed -= RefreshControls;
        _themeManager.ThemeChanged -= OnThemeChanged;
    }
}
