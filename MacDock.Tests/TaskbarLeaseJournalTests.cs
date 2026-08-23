using System.Text;
using System.Text.Json.Nodes;
using MacDock.Core.Interop;
using MacDock.Core.Services.Taskbar;
using Xunit;

namespace MacDock.Tests;

public sealed class TaskbarLeaseJournalTests : IDisposable
{
    private const string ValidLeaseId = "11111111-1111-1111-1111-111111111111";
    private const string ValidLeaseJson =
        "{\"SchemaVersion\":1,\"LeaseId\":\"11111111-1111-1111-1111-111111111111\",\"OwnerProcessId\":10,\"OwnerProcessStartTimeUtcTicks\":20,\"WatchdogProcessId\":null,\"Status\":1,\"Generation\":1,\"UpdatedAtUtc\":\"2026-08-22T00:00:00+00:00\",\"Windows\":[{\"Handle\":42,\"ProcessId\":10,\"ProcessStartTimeUtcTicks\":20,\"ClassName\":\"Shell_TrayWnd\",\"MonitorHandle\":30,\"WasVisible\":true,\"ShowCommand\":5,\"MutationState\":2}]}";

    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(), $"macdock-task2-journal-{Guid.NewGuid():N}");

    public TaskbarLeaseJournalTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void WriteThenRead_RoundTripsCompleteLease()
    {
        var path = Path.Combine(_tempDirectory, "taskbar-lease.json");
        var journal = new TaskbarLeaseJournal(path);
        var document = LeaseSamples.Active(ValidLeaseId, handle: 42);

        journal.Write(document);

        var loaded = Assert.IsType<TaskbarLeaseDocument>(journal.Read());
        Assert.Equal(document.LeaseId, loaded.LeaseId);
        Assert.Equal(document.Status, loaded.Status);
        Assert.Equal(document.OwnerProcessId, loaded.OwnerProcessId);
        Assert.Equal(document.OwnerProcessStartTimeUtcTicks, loaded.OwnerProcessStartTimeUtcTicks);
        Assert.Equal(document.WatchdogProcessId, loaded.WatchdogProcessId);
        Assert.Equal(document.Generation, loaded.Generation);
        Assert.Equal(document.UpdatedAtUtc, loaded.UpdatedAtUtc);
        Assert.Equal(document.Windows.ToArray(), loaded.Windows.ToArray());
        Assert.Empty(Directory.GetFiles(_tempDirectory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Read_WhenJournalIsMissing_ReturnsNull()
    {
        var path = Path.Combine(_tempDirectory, "missing", "taskbar-lease.json");

        Assert.Null(new TaskbarLeaseJournal(path).Read());
    }

    [Fact]
    public void Read_WhenPathIsAnExistingDirectory_PropagatesThePathError()
    {
        var path = Path.Combine(_tempDirectory, "taskbar-lease-directory");
        Directory.CreateDirectory(path);

        var exception = Record.Exception(() => new TaskbarLeaseJournal(path).Read());

        Assert.NotNull(exception);
        Assert.IsNotType<FileNotFoundException>(exception);
        Assert.IsNotType<DirectoryNotFoundException>(exception);
    }

    [Fact]
    public void Write_ReplacesExistingJournalAtomically()
    {
        var path = Path.Combine(_tempDirectory, "taskbar-lease.json");
        var journal = new TaskbarLeaseJournal(path);
        var first = LeaseSamples.Active(ValidLeaseId, handle: 42);
        var second = LeaseSamples.Active(ValidLeaseId, handle: 43) with { Generation = 2 };

        journal.Write(first);
        journal.Write(second);

        var loaded = Assert.IsType<TaskbarLeaseDocument>(journal.Read());
        Assert.Equal(2, loaded.Generation);
        Assert.Equal(43, Assert.Single(loaded.Windows).Handle);
        Assert.Empty(Directory.GetFiles(_tempDirectory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Delete_RemovesJournalAndIsIdempotent()
    {
        var path = Path.Combine(_tempDirectory, "taskbar-lease.json");
        var journal = new TaskbarLeaseJournal(path);
        journal.Write(LeaseSamples.Active(ValidLeaseId, handle: 42));

        journal.Delete();
        journal.Delete();

        Assert.False(File.Exists(path));
    }

    [Theory]
    [MemberData(nameof(InvalidLeaseJsonCases))]
    public void Read_InvalidOrUnsupportedLeasePreservesOriginalBytes(string json)
    {
        var path = Path.Combine(_tempDirectory, "taskbar-lease.json");
        var original = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);
        File.WriteAllBytes(path, original);

        Assert.Throws<InvalidDataException>(() => new TaskbarLeaseJournal(path).Read());
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(_tempDirectory, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [MemberData(nameof(MissingRequiredSchemaMembers))]
    public void Read_WhenRequiredSchemaMemberIsMissing_ThrowsAndPreservesOriginalBytes(string json)
    {
        var path = Path.Combine(_tempDirectory, "taskbar-lease.json");
        var original = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);
        File.WriteAllBytes(path, original);

        Assert.Throws<InvalidDataException>(() => new TaskbarLeaseJournal(path).Read());
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void Write_InvalidLeaseDoesNotModifyExistingSource()
    {
        var path = Path.Combine(_tempDirectory, "taskbar-lease.json");
        var original = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            .GetBytes("{\"sentinel\":true}");
        File.WriteAllBytes(path, original);
        var invalid = LeaseSamples.Active(ValidLeaseId, handle: 42) with
        {
            OwnerProcessId = 0,
        };

        Assert.Throws<InvalidDataException>(() => new TaskbarLeaseJournal(path).Write(invalid));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(_tempDirectory, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(TaskbarWindowMutationState.Unchanged)]
    [InlineData(TaskbarWindowMutationState.HidePending)]
    [InlineData(TaskbarWindowMutationState.HiddenByLease)]
    public void Read_VisibleSnapshotWithHideShowCommandIsNotRecoverable(
        TaskbarWindowMutationState mutationState)
    {
        var path = Path.Combine(_tempDirectory, "taskbar-lease.json");
        var json = WithSnapshot(snapshot =>
        {
            snapshot["WasVisible"] = true;
            snapshot["ShowCommand"] = NativeMethods.SW_HIDE;
            snapshot["MutationState"] = (int)mutationState;
        });
        var original = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(json);
        File.WriteAllBytes(path, original);

        Assert.Throws<InvalidDataException>(() => new TaskbarLeaseJournal(path).Read());
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void WriteThenRead_InvisibleUnchangedShellTrayWithShowCommandRoundTrips()
    {
        var path = Path.Combine(_tempDirectory, "taskbar-lease.json");
        var journal = new TaskbarLeaseJournal(path);
        var originalSnapshot = Assert.Single(LeaseSamples.Active(ValidLeaseId, handle: 42).Windows);
        var document = LeaseSamples.Active(ValidLeaseId, handle: 42) with
        {
            Windows =
            [
                originalSnapshot with
                {
                    WasVisible = false,
                    ShowCommand = NativeMethods.SW_SHOW,
                    MutationState = TaskbarWindowMutationState.Unchanged,
                },
            ],
        };

        journal.Write(document);

        var loaded = Assert.IsType<TaskbarLeaseDocument>(journal.Read());
        Assert.Equal(document.Windows.ToArray(), loaded.Windows.ToArray());
    }

    [Fact]
    public void WriteThenRead_AllowsSameHandleAcrossDifferentExplorerIdentities()
    {
        var path = Path.Combine(_tempDirectory, "taskbar-lease.json");
        var journal = new TaskbarLeaseJournal(path);
        var firstSnapshot = Assert.Single(LeaseSamples.Active(ValidLeaseId, handle: 42).Windows);
        var secondSnapshot = firstSnapshot with
        {
            ProcessId = 11,
            ProcessStartTimeUtcTicks = 21,
            MonitorHandle = 31,
        };
        var document = LeaseSamples.Active(ValidLeaseId, handle: 42) with
        {
            Windows = [firstSnapshot, secondSnapshot],
        };

        journal.Write(document);

        var loaded = Assert.IsType<TaskbarLeaseDocument>(journal.Read());
        Assert.Equal(2, loaded.Windows.Count);
        Assert.Equal((uint)10, loaded.Windows[0].ProcessId);
        Assert.Equal(20, loaded.Windows[0].ProcessStartTimeUtcTicks);
        Assert.Equal((uint)11, loaded.Windows[1].ProcessId);
        Assert.Equal(21, loaded.Windows[1].ProcessStartTimeUtcTicks);
    }

    public static IEnumerable<object[]> InvalidLeaseJsonCases()
    {
        yield return ["{broken"];
        yield return ["{\"SchemaVersion\":999}"];
        yield return [WithRoot("LeaseId", "not-a-guid")];
        yield return [WithRoot("LeaseId", "00000000-0000-0000-0000-000000000000")];
        yield return [WithRoot("LeaseId", "")];
        yield return [WithRoot("OwnerProcessId", 0)];
        yield return [WithRoot("OwnerProcessId", -1)];
        yield return [WithRoot("OwnerProcessStartTimeUtcTicks", 0)];
        yield return [WithRoot("OwnerProcessStartTimeUtcTicks", -1)];
        yield return [WithRoot("WatchdogProcessId", 0)];
        yield return [WithRoot("WatchdogProcessId", -1)];
        yield return [WithRoot("Status", 99)];
        yield return [WithRoot("Generation", 0)];
        yield return [WithRoot("Generation", -1)];
        yield return [WithRoot("UpdatedAtUtc", "0001-01-01T00:00:00+00:00")];
        yield return [WithRoot("Windows", null)];
        yield return [WithRoot("Windows", new JsonArray())];
        yield return [WithSnapshot(snapshot => snapshot["Handle"] = 0)];
        yield return [WithSnapshot(snapshot => snapshot["Handle"] = -1)];
        yield return [WithSnapshot(snapshot => snapshot["ProcessId"] = 0)];
        yield return [WithSnapshot(snapshot => snapshot["ProcessStartTimeUtcTicks"] = 0)];
        yield return [WithSnapshot(snapshot => snapshot["ClassName"] = "NotTaskbar")];
        yield return [WithSnapshot(snapshot => snapshot["ClassName"] = "")];
        yield return [WithSnapshot(snapshot => snapshot["MonitorHandle"] = 0)];
        yield return [WithSnapshot(snapshot => snapshot["ShowCommand"] = -1)];
        yield return [WithSnapshot(snapshot => snapshot["ShowCommand"] = 12)];
        yield return [WithSnapshot(snapshot => snapshot["MutationState"] = 99)];
        yield return [WithSnapshot(snapshot =>
        {
            snapshot["WasVisible"] = false;
        })];
        yield return [WithSnapshot(snapshot =>
        {
            snapshot["MutationState"] = 1;
            snapshot["WasVisible"] = false;
        })];
        yield return [WithRoot(root => root.Remove("UpdatedAtUtc"))];
        yield return [WithRoot(root => root.Remove("Windows"))];
        yield return [WithDuplicateSnapshot()];
    }

    public static IEnumerable<object[]> MissingRequiredSchemaMembers()
    {
        var leaseProperties = new[]
        {
            "SchemaVersion",
            "LeaseId",
            "OwnerProcessId",
            "OwnerProcessStartTimeUtcTicks",
            "WatchdogProcessId",
            "Status",
            "Generation",
            "UpdatedAtUtc",
            "Windows",
        };
        var snapshotProperties = new[]
        {
            "Handle",
            "ProcessId",
            "ProcessStartTimeUtcTicks",
            "ClassName",
            "MonitorHandle",
            "WasVisible",
            "ShowCommand",
            "MutationState",
        };

        foreach (var property in leaseProperties)
            yield return [WithRoot(root => root.Remove(property))];

        foreach (var property in snapshotProperties)
            yield return [WithSnapshot(snapshot => snapshot.Remove(property))];
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
            Directory.Delete(_tempDirectory, recursive: true);
    }

    private static string WithRoot(string propertyName, object? value)
    {
        var root = JsonNode.Parse(ValidLeaseJson)!.AsObject();
        root[propertyName] = value switch
        {
            null => null,
            JsonNode node => node,
            _ => JsonValue.Create(value),
        };
        return root.ToJsonString();
    }

    private static string WithRoot(Action<JsonObject> mutation)
    {
        var root = JsonNode.Parse(ValidLeaseJson)!.AsObject();
        mutation(root);
        return root.ToJsonString();
    }

    private static string WithSnapshot(Action<JsonObject> mutation)
    {
        var root = JsonNode.Parse(ValidLeaseJson)!.AsObject();
        mutation(root["Windows"]!.AsArray()[0]!.AsObject());
        return root.ToJsonString();
    }

    private static string WithDuplicateSnapshot()
    {
        var root = JsonNode.Parse(ValidLeaseJson)!.AsObject();
        var windows = root["Windows"]!.AsArray();
        windows.Add(JsonNode.Parse(windows[0]!.ToJsonString()));
        return root.ToJsonString();
    }
}
