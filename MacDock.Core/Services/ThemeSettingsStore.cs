using System.Text.Json;
using System.Text.Json.Serialization;
using MacDock.Core.Models;

namespace MacDock.Core.Services;

/// <summary>读取和原子保存 MacDock 主题偏好。</summary>
public sealed class ThemeSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly AtomicJsonFile<ThemeSettings> _file;

    public ThemeSettingsStore(string filePath)
    {
        _file = new AtomicJsonFile<ThemeSettings>(filePath, JsonOptions);
    }

    /// <summary>读取主题偏好；文件尚不存在时返回“跟随系统”。</summary>
    public ThemeSettings Load()
    {
        try
        {
            var settings = _file.Read();
            Validate(settings);
            return settings;
        }
        catch (FileNotFoundException)
        {
            return new ThemeSettings();
        }
        catch (DirectoryNotFoundException)
        {
            return new ThemeSettings();
        }
    }

    /// <summary>以原子替换方式保存当前主题偏好。</summary>
    public void Save(ThemeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);
        _file.Write(settings);
    }

    private static void Validate(ThemeSettings settings)
    {
        if (settings.SchemaVersion != ThemeSettings.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported theme settings schema version: {settings.SchemaVersion}.");
        }

        if (!Enum.IsDefined(settings.Mode))
            throw new InvalidDataException($"Unsupported theme mode: {settings.Mode}.");
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
        options.Converters.Add(new StrictThemeSettingsJsonConverter());
        return options;
    }

    private sealed class StrictThemeSettingsJsonConverter : JsonConverter<ThemeSettings>
    {
        public override bool HandleNull => true;

        public override ThemeSettings Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("The theme settings document must be a JSON object.");

            int? schemaVersion = null;
            AppThemeMode? mode = null;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    if (!schemaVersion.HasValue)
                        throw new JsonException("The theme settings document is missing SchemaVersion.");
                    if (!mode.HasValue)
                        throw new JsonException("The theme settings document is missing Mode.");

                    return new ThemeSettings
                    {
                        SchemaVersion = schemaVersion.Value,
                        Mode = mode.Value,
                    };
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                    throw new JsonException("The theme settings document contains an invalid member.");

                var propertyName = reader.GetString();
                switch (propertyName)
                {
                    case nameof(ThemeSettings.SchemaVersion):
                        if (schemaVersion.HasValue)
                            throw new JsonException("The theme settings document contains duplicate SchemaVersion.");
                        if (!reader.Read()
                            || reader.TokenType != JsonTokenType.Number
                            || !reader.TryGetInt32(out var parsedSchemaVersion))
                        {
                            throw new JsonException("SchemaVersion must be an integer JSON number.");
                        }

                        schemaVersion = parsedSchemaVersion;
                        break;

                    case nameof(ThemeSettings.Mode):
                        if (mode.HasValue)
                            throw new JsonException("The theme settings document contains duplicate Mode.");
                        if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                            throw new JsonException("Mode must be a JSON string.");

                        var parsedMode = reader.GetString();
                        if (!Enum.TryParse<AppThemeMode>(parsedMode, ignoreCase: false, out var value)
                            || !Enum.IsDefined(value))
                        {
                            throw new JsonException($"Unsupported theme mode '{parsedMode}'.");
                        }

                        mode = value;
                        break;

                    default:
                        throw new JsonException(
                            $"The theme settings document contains unknown member '{propertyName}'.");
                }
            }

            throw new JsonException("The theme settings document ended before its object was complete.");
        }

        public override void Write(
            Utf8JsonWriter writer,
            ThemeSettings value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber(nameof(ThemeSettings.SchemaVersion), value.SchemaVersion);
            writer.WriteString(nameof(ThemeSettings.Mode), value.Mode.ToString());
            writer.WriteEndObject();
        }
    }
}
