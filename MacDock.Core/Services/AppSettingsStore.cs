using System.Text.Json;
using System.Text.Json.Serialization;
using MacDock.Core.Models;

namespace MacDock.Core.Services;

public sealed class AppSettingsStore : IAppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly AtomicJsonFile<AppSettings> _file;

    public AppSettingsStore(string filePath)
    {
        _file = new AtomicJsonFile<AppSettings>(filePath, JsonOptions);
    }

    public AppSettings Load()
    {
        try
        {
            var settings = _file.Read();
            ValidateSchema(settings);
            return settings;
        }
        catch (FileNotFoundException)
        {
            return new AppSettings();
        }
        catch (DirectoryNotFoundException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ValidateSchema(settings);
        _file.Write(settings);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = false,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new StrictAppSettingsJsonConverter());
        return options;
    }

    private static void ValidateSchema(AppSettings settings)
    {
        if (settings.SchemaVersion != AppSettings.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported app settings schema version: {settings.SchemaVersion}.");
        }
    }

    private sealed class StrictAppSettingsJsonConverter : JsonConverter<AppSettings>
    {
        public override bool HandleNull => true;

        public override AppSettings Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
                throw new JsonException("The app settings document must not be null.");

            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("The app settings document must be a JSON object.");

            int? schemaVersion = null;
            bool? hideWindowsTaskbar = null;
            bool? menuBarReserveWorkArea = null;
            bool? trayTakeover = null;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    if (!schemaVersion.HasValue)
                        throw new JsonException("The app settings document is missing SchemaVersion.");

                    if (!hideWindowsTaskbar.HasValue)
                    {
                        throw new JsonException(
                            "The app settings document is missing HideWindowsTaskbar.");
                    }

                    return new AppSettings
                    {
                        SchemaVersion = schemaVersion.Value,
                        HideWindowsTaskbar = hideWindowsTaskbar.Value,
                        // 老配置无键时取默认 true（新字段向后兼容）
                        MenuBarReserveWorkArea = menuBarReserveWorkArea ?? true,
                        TrayTakeover = trayTakeover ?? true,
                    };
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                    throw new JsonException("The app settings document contains an invalid member.");

                var propertyName = reader.GetString();
                if (propertyName is null)
                    throw new JsonException("The app settings document contains an invalid member.");

                switch (propertyName)
                {
                    case nameof(AppSettings.SchemaVersion):
                        if (schemaVersion.HasValue)
                            throw new JsonException("The app settings document contains duplicate SchemaVersion.");

                        if (!reader.Read()
                            || reader.TokenType != JsonTokenType.Number
                            || !reader.TryGetInt32(out var parsedSchemaVersion))
                        {
                            throw new JsonException("SchemaVersion must be an integer JSON number.");
                        }

                        schemaVersion = parsedSchemaVersion;
                        break;

                    case nameof(AppSettings.HideWindowsTaskbar):
                        if (hideWindowsTaskbar.HasValue)
                        {
                            throw new JsonException(
                                "The app settings document contains duplicate HideWindowsTaskbar.");
                        }

                        if (!reader.Read()
                            || (reader.TokenType != JsonTokenType.True
                                && reader.TokenType != JsonTokenType.False))
                        {
                            throw new JsonException("HideWindowsTaskbar must be a JSON boolean.");
                        }

                        hideWindowsTaskbar = reader.GetBoolean();
                        break;

                    case nameof(AppSettings.MenuBarReserveWorkArea):
                        if (menuBarReserveWorkArea.HasValue)
                        {
                            throw new JsonException(
                                "The app settings document contains duplicate MenuBarReserveWorkArea.");
                        }

                        if (!reader.Read()
                            || (reader.TokenType != JsonTokenType.True
                                && reader.TokenType != JsonTokenType.False))
                        {
                            throw new JsonException("MenuBarReserveWorkArea must be a JSON boolean.");
                        }

                        menuBarReserveWorkArea = reader.GetBoolean();
                        break;

                    case nameof(AppSettings.TrayTakeover):
                        if (trayTakeover.HasValue)
                        {
                            throw new JsonException(
                                "The app settings document contains duplicate TrayTakeover.");
                        }

                        if (!reader.Read()
                            || (reader.TokenType != JsonTokenType.True
                                && reader.TokenType != JsonTokenType.False))
                        {
                            throw new JsonException("TrayTakeover must be a JSON boolean.");
                        }

                        trayTakeover = reader.GetBoolean();
                        break;

                    default:
                        throw new JsonException(
                            $"The app settings document contains unknown member '{propertyName}'.");
                }
            }

            throw new JsonException("The app settings document ended before its object was complete.");
        }

        public override void Write(
            Utf8JsonWriter writer,
            AppSettings value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber(nameof(AppSettings.SchemaVersion), value.SchemaVersion);
            writer.WriteBoolean(nameof(AppSettings.HideWindowsTaskbar), value.HideWindowsTaskbar);
            writer.WriteBoolean(
                nameof(AppSettings.MenuBarReserveWorkArea),
                value.MenuBarReserveWorkArea);
            writer.WriteBoolean(
                nameof(AppSettings.TrayTakeover),
                value.TrayTakeover);
            writer.WriteEndObject();
        }
    }
}
