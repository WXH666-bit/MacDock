using MacDock.Core.Models;
using MacDock.Core.Services;

namespace MacDock.Core.Services.Taskbar;

public sealed record TaskbarStartupResult(
    AppSettings Settings,
    bool ChangesAllowed,
    string? Error);

/// <summary>
/// Completes stale taskbar recovery before reading the user's settings.
/// </summary>
public sealed class TaskbarStartupGate
{
    private readonly ITaskbarRecoveryService _recoveryService;
    private readonly IAppSettingsStore _settingsStore;
    private readonly SemaphoreSlim _serial = new(1, 1);

    public TaskbarStartupGate(
        ITaskbarRecoveryService recoveryService,
        IAppSettingsStore settingsStore)
    {
        _recoveryService = recoveryService
            ?? throw new ArgumentNullException(nameof(recoveryService));
        _settingsStore = settingsStore
            ?? throw new ArgumentNullException(nameof(settingsStore));
    }

    public async Task<TaskbarStartupResult> PrepareAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new TaskbarStartupResult(
                new AppSettings(),
                ChangesAllowed: false,
                Error: "Taskbar startup was canceled before recovery began.");
        }

        try
        {
            var changesAllowed = true;
            string? error = null;

            try
            {
                var recovery = await _recoveryService
                    .TryRecoverStaleAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!recovery.Succeeded)
                {
                    changesAllowed = false;
                    error = recovery.Error ?? "Stale taskbar recovery did not complete.";
                }
            }
            catch (OperationCanceledException)
            {
                changesAllowed = false;
                error = "Taskbar startup recovery was canceled.";
            }
            catch (Exception exception)
            {
                changesAllowed = false;
                error = $"Taskbar startup recovery failed: {exception.Message}";
            }

            if (cancellationToken.IsCancellationRequested)
            {
                changesAllowed = false;
                error ??= "Taskbar startup was canceled.";
            }

            AppSettings settings;
            try
            {
                settings = _settingsStore.Load();
                ValidateSettings(settings);
            }
            catch (Exception exception)
            {
                changesAllowed = false;
                error = CombineErrors(error, $"Settings could not be loaded: {exception.Message}");
                settings = new AppSettings();
            }

            if (cancellationToken.IsCancellationRequested)
            {
                changesAllowed = false;
                error ??= "Taskbar startup was canceled.";
            }

            return new TaskbarStartupResult(settings, changesAllowed, error);
        }
        finally
        {
            _serial.Release();
        }
    }

    private static void ValidateSettings(AppSettings? settings)
    {
        if (settings is null)
            throw new InvalidDataException("The app settings store returned no settings.");

        if (settings.SchemaVersion != AppSettings.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported app settings schema version: {settings.SchemaVersion}.");
        }
    }

    private static string CombineErrors(string? first, string second)
        => string.IsNullOrWhiteSpace(first) ? second : $"{first} {second}";
}
