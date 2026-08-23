using System.Text;
using MacDock.Core.Models;
using MacDock.Core.Services;
using Xunit;

namespace MacDock.Tests;

public sealed class AppSettingsStoreTests
{
    [Fact]
    public void NewSettings_DefaultsToCurrentSchemaAndTaskbarTakeoverOff()
    {
        var settings = new AppSettings();

        Assert.Equal(1, AppSettings.CurrentSchemaVersion);
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.False(settings.HideWindowsTaskbar);
    }

    [Fact]
    public void Load_MissingFile_ReturnsFreshDisabledSchemaOneAndCreatesNothing()
    {
        WithTempDirectory(tempDirectory =>
        {
            var path = Path.Combine(tempDirectory, "settings.json");
            IAppSettingsStore store = new AppSettingsStore(path);

            var first = store.Load();
            var second = store.Load();

            Assert.NotSame(first, second);
            Assert.Equal(1, first.SchemaVersion);
            Assert.Equal(1, second.SchemaVersion);
            Assert.False(first.HideWindowsTaskbar);
            Assert.False(second.HideWindowsTaskbar);
            Assert.False(File.Exists(path));
            Assert.Empty(Directory.GetFileSystemEntries(tempDirectory, "*", SearchOption.AllDirectories));
        });
    }

    [Fact]
    public void Load_MissingParentDirectory_ReturnsFreshDisabledSchemaOneAndCreatesNothing()
    {
        WithTempDirectory(tempDirectory =>
        {
            var missingParent = Path.Combine(tempDirectory, "missing", "parent");
            var path = Path.Combine(missingParent, "settings.json");
            IAppSettingsStore store = new AppSettingsStore(path);

            var first = store.Load();
            var second = store.Load();

            Assert.NotSame(first, second);
            Assert.Equal(1, first.SchemaVersion);
            Assert.Equal(1, second.SchemaVersion);
            Assert.False(first.HideWindowsTaskbar);
            Assert.False(second.HideWindowsTaskbar);
            Assert.False(Directory.Exists(missingParent));
            Assert.False(File.Exists(path));
            Assert.Empty(Directory.GetFileSystemEntries(tempDirectory, "*", SearchOption.AllDirectories));
        });
    }

    [Fact]
    public void SaveThenLoad_True_RoundTripsWithoutTempResidue()
    {
        WithTempDirectory(tempDirectory =>
        {
            var path = Path.Combine(tempDirectory, "settings.json");
            var store = new AppSettingsStore(path);
            var settings = new AppSettings { HideWindowsTaskbar = true };

            store.Save(settings);
            var loaded = store.Load();

            Assert.Equal(1, loaded.SchemaVersion);
            Assert.True(loaded.HideWindowsTaskbar);
            Assert.Empty(Directory.GetFiles(tempDirectory, "*.tmp", SearchOption.AllDirectories));
        });
    }

    [Fact]
    public void SaveThenLoad_False_RoundTripsWithoutTempResidue()
    {
        WithTempDirectory(tempDirectory =>
        {
            var path = Path.Combine(tempDirectory, "settings.json");
            var store = new AppSettingsStore(path);
            var settings = new AppSettings { HideWindowsTaskbar = false };

            store.Save(settings);
            var loaded = store.Load();

            Assert.Equal(1, loaded.SchemaVersion);
            Assert.False(loaded.HideWindowsTaskbar);
            Assert.Empty(Directory.GetFiles(tempDirectory, "*.tmp", SearchOption.AllDirectories));
        });
    }

    [Fact]
    public void Save_CreatesNestedParentAndEmitsStrictCurrentSchemaJson()
    {
        WithTempDirectory(tempDirectory =>
        {
            var path = Path.Combine(tempDirectory, "nested", "settings", "settings.json");
            var store = new AppSettingsStore(path);

            store.Save(new AppSettings { HideWindowsTaskbar = true });

            Assert.True(Directory.Exists(Path.GetDirectoryName(path)));
            Assert.Equal(
                "{\"SchemaVersion\":1,\"HideWindowsTaskbar\":true,\"MenuBarReserveWorkArea\":true}",
                Utf8.GetString(File.ReadAllBytes(path)));
            Assert.Empty(Directory.GetFiles(tempDirectory, "*.tmp", SearchOption.AllDirectories));
        });
    }

    [Fact]
    public void Save_ReplacesExistingValidSettingsAtomicallyWithoutTempResidue()
    {
        WithTempDirectory(tempDirectory =>
        {
            var path = Path.Combine(tempDirectory, "settings.json");
            var store = new AppSettingsStore(path);

            store.Save(new AppSettings { HideWindowsTaskbar = false });
            var firstBytes = File.ReadAllBytes(path);

            store.Save(new AppSettings { HideWindowsTaskbar = true });
            var secondBytes = File.ReadAllBytes(path);

            Assert.False(firstBytes.SequenceEqual(secondBytes));
            Assert.True(store.Load().HideWindowsTaskbar);
            Assert.Empty(Directory.GetFiles(tempDirectory, "*.tmp", SearchOption.AllDirectories));
        });
    }

    [Fact]
    public void Load_ValidMembersMayAppearInEitherOrder()
    {
        WithTempDirectory(tempDirectory =>
        {
            var path = Path.Combine(tempDirectory, "settings.json");
            WriteUtf8(path, "{\"HideWindowsTaskbar\":true,\"SchemaVersion\":1}");

            var loaded = new AppSettingsStore(path).Load();

            Assert.Equal(1, loaded.SchemaVersion);
            Assert.True(loaded.HideWindowsTaskbar);
        });
    }

    [Fact]
    public void Load_DoesNotCacheMutableSettingsBetweenCalls()
    {
        WithTempDirectory(tempDirectory =>
        {
            var path = Path.Combine(tempDirectory, "settings.json");
            var store = new AppSettingsStore(path);
            store.Save(new AppSettings { HideWindowsTaskbar = true });

            var first = store.Load();
            first.HideWindowsTaskbar = false;
            var second = store.Load();

            Assert.True(second.HideWindowsTaskbar);
            Assert.NotSame(first, second);
        });
    }

    [Fact]
    public void Load_SharingFailure_PropagatesInsteadOfReturningDefault()
    {
        WithTempDirectory(tempDirectory =>
        {
            var path = Path.Combine(tempDirectory, "settings.json");
            var originalBytes = WriteUtf8(path, "{\"SchemaVersion\":1,\"HideWindowsTaskbar\":false}");
            var store = new AppSettingsStore(path);

            using (var heldOpen = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.None))
            {
                Assert.ThrowsAny<IOException>(() => store.Load());
            }

            Assert.Equal(originalBytes, File.ReadAllBytes(path));
        });
    }

    [Fact]
    public void Save_DoesNotMutateCallerSettings()
    {
        WithTempDirectory(tempDirectory =>
        {
            var path = Path.Combine(tempDirectory, "settings.json");
            var store = new AppSettingsStore(path);
            var settings = new AppSettings
            {
                SchemaVersion = AppSettings.CurrentSchemaVersion,
                HideWindowsTaskbar = true,
            };

            store.Save(settings);

            Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
            Assert.True(settings.HideWindowsTaskbar);
        });
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"SchemaVersion\":1,\"HideWindowsTaskbar\":false} trailing")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("7")]
    [InlineData("\"settings\"")]
    [InlineData("{\"SchemaVersion\":1}")]
    [InlineData("{\"HideWindowsTaskbar\":false}")]
    [InlineData("{\"schemaversion\":1,\"HideWindowsTaskbar\":false}")]
    [InlineData("{\"SchemaVersion\":1,\"hidewindowstaskbar\":false}")]
    [InlineData("{\"SchemaVersion\":\"1\",\"HideWindowsTaskbar\":false}")]
    [InlineData("{\"SchemaVersion\":true,\"HideWindowsTaskbar\":false}")]
    [InlineData("{\"SchemaVersion\":1.0,\"HideWindowsTaskbar\":false}")]
    [InlineData("{\"SchemaVersion\":1e0,\"HideWindowsTaskbar\":false}")]
    [InlineData("{\"SchemaVersion\":1,\"HideWindowsTaskbar\":\"false\"}")]
    [InlineData("{\"SchemaVersion\":1,\"HideWindowsTaskbar\":0}")]
    [InlineData("{\"SchemaVersion\":null,\"HideWindowsTaskbar\":false}")]
    [InlineData("{\"SchemaVersion\":1,\"HideWindowsTaskbar\":null}")]
    [InlineData("{\"SchemaVersion\":1,\"HideWindowsTaskbar\":false,\"Extra\":0}")]
    [InlineData("{\"SchemaVersion\":1,\"SchemaVersion\":1,\"HideWindowsTaskbar\":false}")]
    [InlineData("{\"SchemaVersion\":1,\"HideWindowsTaskbar\":false,\"HideWindowsTaskbar\":true}")]
    [InlineData("{\"SchemaVersion\":1,\"HideWindowsTaskbar\":false,}")]
    public void Load_InvalidJson_ThrowsAndPreservesExactSourceBytes(string json)
    {
        WithTempDirectory(tempDirectory =>
        {
            var path = Path.Combine(tempDirectory, "settings.json");
            var originalBytes = WriteUtf8(path, json);

            Assert.Throws<InvalidDataException>(() => new AppSettingsStore(path).Load());

            Assert.Equal(originalBytes, File.ReadAllBytes(path));
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Load_UnsupportedSchema_ThrowsAndPreservesExactSourceBytes(int schemaVersion)
    {
        WithTempDirectory(tempDirectory =>
        {
            var path = Path.Combine(tempDirectory, "settings.json");
            var originalBytes = WriteUtf8(
                path,
                $"{{\"SchemaVersion\":{schemaVersion},\"HideWindowsTaskbar\":true}}");

            Assert.Throws<InvalidDataException>(() => new AppSettingsStore(path).Load());

            Assert.Equal(originalBytes, File.ReadAllBytes(path));
        });
    }

    [Fact]
    public void Save_Null_ThrowsArgumentNullExceptionWithoutCreatingStorage()
    {
        WithTempDirectory(tempDirectory =>
        {
            var path = Path.Combine(tempDirectory, "settings.json");
            var store = new AppSettingsStore(path);

            Assert.Throws<ArgumentNullException>(() => store.Save(null!));

            Assert.False(File.Exists(path));
            Assert.Empty(Directory.GetFileSystemEntries(tempDirectory, "*", SearchOption.AllDirectories));
        });
    }

    [Fact]
    public void Save_UnsupportedSchema_RejectsBeforeChangingExistingFile()
    {
        WithTempDirectory(tempDirectory =>
        {
            var path = Path.Combine(tempDirectory, "settings.json");
            var originalBytes = WriteUtf8(path, "{\"SchemaVersion\":1,\"HideWindowsTaskbar\":false}");
            var store = new AppSettingsStore(path);
            var unsupported = new AppSettings { SchemaVersion = 2, HideWindowsTaskbar = true };

            Assert.Throws<InvalidDataException>(() => store.Save(unsupported));

            Assert.Equal(originalBytes, File.ReadAllBytes(path));
            Assert.Equal(2, unsupported.SchemaVersion);
            Assert.True(unsupported.HideWindowsTaskbar);
            Assert.Empty(Directory.GetFiles(tempDirectory, "*.tmp", SearchOption.AllDirectories));
        });
    }

    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static byte[] WriteUtf8(string path, string json)
    {
        var bytes = Utf8.GetBytes(json);
        File.WriteAllBytes(path, bytes);
        return bytes;
    }

    private static void WithTempDirectory(Action<string> test)
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(),
            $"macdock-task5-{Guid.NewGuid():N}");
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
