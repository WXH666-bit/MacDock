using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MacDock.Core.Services;

public sealed class AtomicJsonFile<T>
{
    private static readonly JsonSerializerOptions DefaultJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _path;
    private readonly JsonSerializerOptions _jsonOptions;

    public AtomicJsonFile(string path, JsonSerializerOptions? jsonOptions = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A JSON file path is required.", nameof(path));

        _path = path;
        _jsonOptions = jsonOptions ?? DefaultJsonOptions;
    }

    public string FilePath => _path;

    public T Read()
    {
        try
        {
            using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                options: FileOptions.SequentialScan);

            var value = JsonSerializer.Deserialize<T>(stream, _jsonOptions);
            if (value is null)
                throw new InvalidDataException("The JSON document must not be null.");

            return value;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The JSON file '{_path}' is invalid.", exception);
        }
        catch (NotSupportedException exception)
        {
            throw new InvalidDataException($"The JSON file '{_path}' is not supported.", exception);
        }
    }

    public void Write(T value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var parentDirectory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(parentDirectory))
            Directory.CreateDirectory(parentDirectory);

        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        var temporaryFileCreated = false;

        try
        {
            var json = JsonSerializer.Serialize(value, _jsonOptions);
            var bytes = Utf8NoBom.GetBytes(json);

            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       options: FileOptions.WriteThrough))
            {
                temporaryFileCreated = true;
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
                File.Replace(temporaryPath, _path, destinationBackupFileName: null);
            else
                File.Move(temporaryPath, _path);
        }
        finally
        {
            if (temporaryFileCreated)
                File.Delete(temporaryPath);
        }
    }
}
