using System.Text;
using MacDock.Core.Models;
using MacDock.Core.Services;
using Xunit;

namespace MacDock.Tests;

public sealed class ThemeSettingsStoreTests
{
    [Fact]
    public void Load_MissingFile_ReturnsSystemWithoutCreatingStorage()
    {
        WithTempDirectory(tempDirectory =>
        {
            var path = Path.Combine(tempDirectory, "theme.json");

            var settings = new ThemeSettingsStore(path).Load();

            Assert.Equal(ThemeSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.Equal(AppThemeMode.System, settings.Mode);
            Assert.False(File.Exists(path));
        });
    }

    [Theory]
    [InlineData(AppThemeMode.System)]
    [InlineData(AppThemeMode.Light)]
    [InlineData(AppThemeMode.Dark)]
    public void SaveThenLoad_RoundTripsWithoutTempResidue(AppThemeMode mode)
    {
        WithTempDirectory(tempDirectory =>
        {
            var path = Path.Combine(tempDirectory, "theme.json");
            var store = new ThemeSettingsStore(path);

            store.Save(new ThemeSettings { Mode = mode });
            var loaded = store.Load();

            Assert.Equal(mode, loaded.Mode);
            Assert.Equal(
                $"{{\"SchemaVersion\":1,\"Mode\":\"{mode}\"}}",
                Encoding.UTF8.GetString(File.ReadAllBytes(path)));
            Assert.Empty(Directory.GetFiles(tempDirectory, "*.tmp", SearchOption.AllDirectories));
        });
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"SchemaVersion\":1}")]
    [InlineData("{\"Mode\":\"System\"}")]
    [InlineData("{\"SchemaVersion\":1,\"Mode\":\"system\"}")]
    [InlineData("{\"SchemaVersion\":1,\"Mode\":0}")]
    [InlineData("{\"SchemaVersion\":1,\"Mode\":\"Blue\"}")]
    [InlineData("{\"SchemaVersion\":1,\"Mode\":\"Dark\",\"Extra\":true}")]
    public void Load_InvalidDocument_ThrowsAndPreservesSource(string json)
    {
        WithTempDirectory(tempDirectory =>
        {
            var path = Path.Combine(tempDirectory, "theme.json");
            var original = Encoding.UTF8.GetBytes(json);
            File.WriteAllBytes(path, original);

            Assert.Throws<InvalidDataException>(() => new ThemeSettingsStore(path).Load());

            Assert.Equal(original, File.ReadAllBytes(path));
        });
    }

    [Fact]
    public void Save_UnsupportedMode_DoesNotChangeExistingFile()
    {
        WithTempDirectory(tempDirectory =>
        {
            var path = Path.Combine(tempDirectory, "theme.json");
            var original = Encoding.UTF8.GetBytes("{\"SchemaVersion\":1,\"Mode\":\"Light\"}");
            File.WriteAllBytes(path, original);

            Assert.Throws<InvalidDataException>(() => new ThemeSettingsStore(path).Save(
                new ThemeSettings { Mode = (AppThemeMode)99 }));

            Assert.Equal(original, File.ReadAllBytes(path));
        });
    }

    private static void WithTempDirectory(Action<string> test)
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            $"macdock-theme-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            test(tempDirectory);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }
}
