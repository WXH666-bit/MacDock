using System.Text.Json.Serialization;

namespace MacDock.Core.Services.Taskbar;

public enum TaskbarLeaseStatus
{
    Prepared,
    Active,
    Releasing,
}

public sealed record TaskbarLeaseDocument(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string LeaseId,
    [property: JsonRequired] int OwnerProcessId,
    [property: JsonRequired] long OwnerProcessStartTimeUtcTicks,
    [property: JsonRequired] int? WatchdogProcessId,
    [property: JsonRequired] TaskbarLeaseStatus Status,
    [property: JsonRequired] long Generation,
    [property: JsonRequired] DateTimeOffset UpdatedAtUtc,
    [property: JsonRequired] IReadOnlyList<TaskbarWindowSnapshot> Windows)
{
    public const int CurrentSchemaVersion = 1;
}
