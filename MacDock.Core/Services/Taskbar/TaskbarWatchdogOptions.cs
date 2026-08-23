using System.Globalization;

namespace MacDock.Core.Services.Taskbar;

public sealed record TaskbarWatchdogOptions(
    int ParentProcessId,
    long ParentProcessStartTimeUtcTicks,
    string LeaseId,
    string JournalPath,
    string ReadyEventName,
    string StopEventName)
{
    private const string JournalFileName = "taskbar-lease.json";
    private const string JournalDirectoryName = "MacDock";
    private const string LocalEventPrefix = "Local\\MacDock.Taskbar.";
    private const int EventTokenLength = 32;

    private static readonly string[] RequiredKeys =
    [
        "--parent-pid",
        "--parent-start-ticks",
        "--lease-id",
        "--journal",
        "--ready-event",
        "--stop-event",
    ];

    public static bool TryParse(
        IReadOnlyList<string>? args,
        string? appDataRoot,
        out TaskbarWatchdogOptions? options,
        out string? error)
    {
        options = null;
        error = null;

        if (args is null || args.Count != RequiredKeys.Length * 2)
            return Reject("Exactly six option/value pairs are required.", out error);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Count; index += 2)
        {
            var key = args[index];
            var value = args[index + 1];
            if (string.IsNullOrWhiteSpace(key)
                || !key.StartsWith("--", StringComparison.Ordinal)
                || !RequiredKeys.Contains(key, StringComparer.Ordinal))
            {
                return Reject("An unknown or malformed option was supplied.", out error);
            }

            if (!values.TryAdd(key, value ?? string.Empty))
                return Reject("Each option must appear exactly once.", out error);
        }

        foreach (var requiredKey in RequiredKeys)
        {
            if (!values.ContainsKey(requiredKey))
                return Reject("All required options must be supplied.", out error);
        }

        if (!int.TryParse(
                values["--parent-pid"],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parentProcessId)
            || parentProcessId <= 0)
        {
            return Reject("The parent process ID must be a positive integer.", out error);
        }

        if (!long.TryParse(
                values["--parent-start-ticks"],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parentProcessStartTimeUtcTicks)
            || parentProcessStartTimeUtcTicks <= 0)
        {
            return Reject("The parent process start ticks must be positive.", out error);
        }

        var leaseId = values["--lease-id"];
        if (string.IsNullOrWhiteSpace(leaseId)
            || !Guid.TryParse(leaseId, out var parsedLeaseId)
            || parsedLeaseId == Guid.Empty)
        {
            return Reject("The lease ID must be a non-empty GUID.", out error);
        }

        if (!TryCanonicalizeJournalPath(
                values["--journal"],
                appDataRoot,
                out var journalPath))
        {
            return Reject("The journal must be the exact MacDock taskbar lease path.", out error);
        }

        if (!TryValidateEventNames(
                values["--ready-event"],
                values["--stop-event"]))
        {
            return Reject("Ready and stop must be distinct Local MacDock event names.", out error);
        }

        options = new TaskbarWatchdogOptions(
            parentProcessId,
            parentProcessStartTimeUtcTicks,
            leaseId,
            journalPath,
            values["--ready-event"],
            values["--stop-event"]);
        return true;
    }

    private static bool TryCanonicalizeJournalPath(
        string? suppliedPath,
        string? appDataRoot,
        out string canonicalPath)
    {
        canonicalPath = string.Empty;
        if (string.IsNullOrWhiteSpace(suppliedPath)
            || string.IsNullOrWhiteSpace(appDataRoot)
            || !Path.IsPathFullyQualified(suppliedPath)
            || !Path.IsPathFullyQualified(appDataRoot)
            || ContainsTraversalSegment(suppliedPath)
            || ContainsInvalidPathCharacter(suppliedPath)
            || ContainsInvalidPathCharacter(appDataRoot))
        {
            return false;
        }

        try
        {
            var expectedPath = Path.GetFullPath(
                Path.Combine(appDataRoot, JournalDirectoryName, JournalFileName));
            canonicalPath = Path.GetFullPath(suppliedPath);
            return string.Equals(
                canonicalPath,
                expectedPath,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            canonicalPath = string.Empty;
            return false;
        }
        catch (NotSupportedException)
        {
            canonicalPath = string.Empty;
            return false;
        }
        catch (IOException)
        {
            canonicalPath = string.Empty;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            canonicalPath = string.Empty;
            return false;
        }
        catch (System.Security.SecurityException)
        {
            canonicalPath = string.Empty;
            return false;
        }
    }

    private static bool TryValidateEventNames(
        string? readyEventName,
        string? stopEventName)
    {
        if (string.IsNullOrWhiteSpace(readyEventName)
            || string.IsNullOrWhiteSpace(stopEventName)
            || string.Equals(readyEventName, stopEventName, StringComparison.Ordinal))
        {
            return false;
        }

        return TryGetEventToken(readyEventName, "ready", out var readyToken)
            && TryGetEventToken(stopEventName, "stop", out var stopToken)
            && string.Equals(readyToken, stopToken, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetEventToken(
        string eventName,
        string expectedSuffix,
        out string token)
    {
        token = string.Empty;
        var suffix = $".{expectedSuffix}";
        var expectedLength = LocalEventPrefix.Length + EventTokenLength + suffix.Length;
        if (eventName.Length != expectedLength
            || !eventName.StartsWith(LocalEventPrefix, StringComparison.Ordinal)
            || !eventName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var tokenStart = LocalEventPrefix.Length;
        token = eventName.Substring(tokenStart, EventTokenLength);
        return token.All(IsHexDigit);
    }

    private static bool ContainsInvalidPathCharacter(string value)
    {
        foreach (var character in value)
        {
            if (character is '<' or '>' or '|' or '"' or '?' or '*' or '\0')
                return true;
        }

        return false;
    }

    private static bool ContainsTraversalSegment(string value)
        => value
            .Split(['\\', '/'], StringSplitOptions.None)
            .Any(segment => string.Equals(segment, ".", StringComparison.Ordinal)
                || string.Equals(segment, "..", StringComparison.Ordinal));

    private static bool IsHexDigit(char value)
        => value is >= '0' and <= '9'
            or >= 'a' and <= 'f'
            or >= 'A' and <= 'F';

    private static bool Reject(string message, out string? error)
    {
        error = message;
        return false;
    }
}
