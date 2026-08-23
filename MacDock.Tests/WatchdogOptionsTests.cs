using MacDock.Core.Services.Taskbar;
using Xunit;

namespace MacDock.Tests;

public sealed class WatchdogOptionsTests
{
    private static readonly string[] RequiredKeys =
    [
        "--parent-pid",
        "--parent-start-ticks",
        "--lease-id",
        "--journal",
        "--ready-event",
        "--stop-event",
    ];

    [Fact]
    public void Parse_ValidRoundTripReturnsCanonicalValues()
    {
        var args = WatchdogSamples.ValidArgs();

        var parsed = TaskbarWatchdogOptions.TryParse(
            args,
            WatchdogSamples.AppDataRoot,
            out var options,
            out var error);

        Assert.True(parsed, error);
        Assert.NotNull(options);
        Assert.Null(error);
        Assert.Equal(1234, options!.ParentProcessId);
        Assert.Equal(638000000000000000, options.ParentProcessStartTimeUtcTicks);
        Assert.Equal(WatchdogSamples.LeaseId, options.LeaseId);
        Assert.Equal(
            Path.GetFullPath(WatchdogSamples.JournalPath),
            options.JournalPath);
        Assert.Equal(WatchdogSamples.ReadyEventName, options.ReadyEventName);
        Assert.Equal(WatchdogSamples.StopEventName, options.StopEventName);
    }

    [Theory]
    [MemberData(nameof(RequiredOptionNames))]
    public void Parse_RejectsMissingRequiredOption(string missingKey)
    {
        var args = WatchdogSamples.RemovePair(WatchdogSamples.ValidArgs(), missingKey);

        AssertRejected(args);
    }

    [Theory]
    [MemberData(nameof(RequiredOptionNames))]
    public void Parse_RejectsDuplicateOption(string duplicateKey)
    {
        var args = WatchdogSamples.ValidArgs().ToList();
        args.Add(duplicateKey);
        args.Add("duplicate");

        AssertRejected(args);
    }

    [Fact]
    public void Parse_RejectsExtraOption()
    {
        var args = WatchdogSamples.ValidArgs().Concat(["--unexpected", "value"]).ToArray();

        AssertRejected(args);
    }

    [Fact]
    public void Parse_RejectsOddInput()
    {
        AssertRejected(WatchdogSamples.ValidArgs().Take(WatchdogSamples.ValidArgs().Length - 1));
    }

    [Theory]
    [InlineData("--parent-pid", "0")]
    [InlineData("--parent-pid", "-1")]
    [InlineData("--parent-pid", "not-a-number")]
    [InlineData("--parent-start-ticks", "0")]
    [InlineData("--parent-start-ticks", "-1")]
    [InlineData("--parent-start-ticks", "not-a-number")]
    [InlineData("--lease-id", "")]
    [InlineData("--lease-id", "not-a-guid")]
    [InlineData("--lease-id", "00000000-0000-0000-0000-000000000000")]
    public void Parse_RejectsInvalidIdentityValues(string key, string value)
    {
        AssertRejected(WatchdogSamples.Replace(WatchdogSamples.ValidArgs(), key, value));
    }

    [Theory]
    [MemberData(nameof(InvalidJournalPaths))]
    public void Parse_RejectsJournalOutsideExactMacDockPath(string journalPath)
    {
        AssertRejected(WatchdogSamples.Replace(
            WatchdogSamples.ValidArgs(),
            "--journal",
            journalPath));
    }

    [Fact]
    public void Parse_AcceptsCaseOnlyEquivalentWindowsJournalPath()
    {
        var caseOnlyPath = WatchdogSamples.JournalPath.ToUpperInvariant();
        var parsed = TaskbarWatchdogOptions.TryParse(
            WatchdogSamples.Replace(WatchdogSamples.ValidArgs(), "--journal", caseOnlyPath),
            WatchdogSamples.AppDataRoot,
            out var options,
            out var error);

        Assert.True(parsed, error);
        Assert.NotNull(options);
        Assert.Equal(
            WatchdogSamples.JournalPath,
            options!.JournalPath,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RejectsOverlongFullyQualifiedJournalPathWithoutThrowing()
    {
        var overlongPath = string.Concat(
            WatchdogSamples.AppDataRoot,
            "\\MacDock\\",
            new string('a', 32768),
            "\\taskbar-lease.json");

        AssertRejected(WatchdogSamples.Replace(
            WatchdogSamples.ValidArgs(),
            "--journal",
            overlongPath));
    }

    [Theory]
    [MemberData(nameof(InvalidEventPairs))]
    public void Parse_RejectsMalformedOrUnsafeEventNames(string readyEventName, string stopEventName)
    {
        var args = WatchdogSamples.Replace(
            WatchdogSamples.ValidArgs(),
            "--ready-event",
            readyEventName);
        args = WatchdogSamples.Replace(args, "--stop-event", stopEventName);

        AssertRejected(args);
    }

    public static IEnumerable<object[]> RequiredOptionNames()
        => RequiredKeys.Select(key => new object[] { key });

    public static IEnumerable<object[]> InvalidJournalPaths()
    {
        yield return [@"C:\Windows\Temp\lease.json"];
        yield return ["relative\\MacDock\\taskbar-lease.json"];
        yield return [WatchdogSamples.AppDataRoot + "-sibling\\MacDock\\taskbar-lease.json"];
        yield return [Path.Combine(WatchdogSamples.AppDataRoot, "..", "MacDock", "taskbar-lease.json")];
        yield return [Path.Combine(WatchdogSamples.AppDataRoot, "MacDock", "sub", "..", "taskbar-lease.json")];
        yield return [Path.Combine(WatchdogSamples.AppDataRoot, "MacDock", "taskbar-lease?.json")];
        yield return [Path.Combine(WatchdogSamples.AppDataRoot, "MacDock", "other.json")];
        yield return [$"{WatchdogSamples.JournalPath}:stream"];
    }

    public static IEnumerable<object[]> InvalidEventPairs()
    {
        var token = "0123456789abcdef0123456789abcdef";
        var otherToken = "fedcba9876543210fedcba9876543210";
        var localPrefix = "Local\\MacDock.Taskbar.";

        yield return [$"Global\\MacDock.Taskbar.{token}.ready", $"Local\\MacDock.Taskbar.{token}.stop"];
        yield return [$"Local\\MacDock.Taskbar.short.ready", $"Local\\MacDock.Taskbar.{token}.stop"];
        yield return [$"Local\\MacDock.Taskbar.{token}.ready", $"Local\\MacDock.Taskbar.{token}.ready"];
        yield return [$"Local\\MacDock.Taskbar.{token}.ready", $"Local\\MacDock.Taskbar.{token}.abort"];
        yield return [$"Local\\MacDock.Taskbar.{token}.ready", $"Local\\MacDock.Taskbar.{otherToken}.stop"];
        yield return [$"Local\\arbitrary.{token}.ready", $"Local\\MacDock.Taskbar.{token}.stop"];
        yield return [$"{localPrefix}{token}.ready", $"{localPrefix}{token}.stop\\extra"];
    }

    private static void AssertRejected(IEnumerable<string> args)
    {
        var parsed = TaskbarWatchdogOptions.TryParse(
            args.ToArray(),
            WatchdogSamples.AppDataRoot,
            out var options,
            out var error);

        Assert.False(parsed, error);
        Assert.Null(options);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}

internal static class WatchdogSamples
{
    public static readonly string AppDataRoot = Path.Combine(
        Path.GetTempPath(),
        "MacDock.TaskbarWatchdogTests",
        $"AppData-{Guid.NewGuid():N}");

    public static string JournalPath
        => Path.Combine(AppDataRoot, "MacDock", "taskbar-lease.json");

    public const string LeaseId = "11111111-1111-1111-1111-111111111111";

    public const string ReadyEventName =
        "Local\\MacDock.Taskbar.0123456789abcdef0123456789abcdef.ready";

    public const string StopEventName =
        "Local\\MacDock.Taskbar.0123456789abcdef0123456789abcdef.stop";

    public static string[] ValidArgs()
        =>
        [
            "--parent-pid",
            "1234",
            "--parent-start-ticks",
            "638000000000000000",
            "--lease-id",
            LeaseId,
            "--journal",
            JournalPath,
            "--ready-event",
            ReadyEventName,
            "--stop-event",
            StopEventName,
        ];

    public static TaskbarRecoveryGuardRequest Request()
        => new(
            LeaseId,
            OwnerProcessId: 1234,
            OwnerProcessStartTimeUtcTicks: 638000000000000000,
            JournalPath: JournalPath);

    public static string[] Replace(string[] source, string key, string value)
    {
        var result = source.ToArray();
        var index = Array.IndexOf(result, key);
        Assert.True(index >= 0, $"The test fixture does not contain {key}.");
        result[index + 1] = value;
        return result;
    }

    public static string[] RemovePair(string[] source, string key)
    {
        var index = Array.IndexOf(source, key);
        Assert.True(index >= 0, $"The test fixture does not contain {key}.");
        return source
            .Take(index)
            .Concat(source.Skip(index + 2))
            .ToArray();
    }
}
