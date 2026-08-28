namespace MacDock.Core.Models;

/// <summary>MacDock 可选的应用主题模式。</summary>
public enum AppThemeMode
{
    /// <summary>跟随 Windows 的应用主题。</summary>
    System,

    /// <summary>始终使用浅色主题。</summary>
    Light,

    /// <summary>始终使用深色主题。</summary>
    Dark,
}

/// <summary>独立于高风险 Shell 设置持久化的主题偏好。</summary>
public sealed class ThemeSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public AppThemeMode Mode { get; set; } = AppThemeMode.System;
}
