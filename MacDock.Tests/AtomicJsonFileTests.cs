using System.Text;
using MacDock.Core.Services;
using Xunit;

namespace MacDock.Tests;

public sealed class AtomicJsonFileTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(), $"macdock-task2-atomic-{Guid.NewGuid():N}");

    public AtomicJsonFileTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void WriteThenRead_CreatesParentAndRoundTripsUtf8Json()
    {
        var path = Path.Combine(_tempDirectory, "nested", "payload.json");
        var expected = new AtomicPayload("中文 ☕", 7);
        var file = new AtomicJsonFile<AtomicPayload>(path);

        file.Write(expected);

        var bytes = File.ReadAllBytes(path);
        var loaded = file.Read();

        Assert.Equal(expected, loaded);
        Assert.Equal((byte)'{', bytes[0]);
        Assert.NotEmpty(bytes);
        Assert.Empty(Directory.GetFiles(_tempDirectory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Write_ReplacesExistingDestinationWithoutLeavingTempFile()
    {
        var path = Path.Combine(_tempDirectory, "payload.json");
        var file = new AtomicJsonFile<AtomicPayload>(path);
        var first = new AtomicPayload("first", 1);
        var second = new AtomicPayload("second", 2);

        file.Write(first);
        var firstBytes = File.ReadAllBytes(path);

        file.Write(second);

        Assert.NotEqual(firstBytes, File.ReadAllBytes(path));
        Assert.Equal(second, file.Read());
        Assert.Empty(Directory.GetFiles(_tempDirectory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Read_MalformedJsonThrowsInvalidDataAndPreservesSourceBytes()
    {
        var path = Path.Combine(_tempDirectory, "payload.json");
        var original = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes("{broken");
        File.WriteAllBytes(path, original);
        var file = new AtomicJsonFile<AtomicPayload>(path);

        Assert.Throws<InvalidDataException>(() => file.Read());
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }

    private sealed record AtomicPayload(string Text, int Count);
}
