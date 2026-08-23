using System.Text.Json.Serialization;

namespace MacDock.Core.Services.Taskbar;

public enum TaskbarWindowMutationState
{
    Unchanged,
    HidePending,
    HiddenByLease,
}

public sealed record TaskbarWindowSnapshot(
    [property: JsonRequired] long Handle,
    [property: JsonRequired] uint ProcessId,
    [property: JsonRequired] long ProcessStartTimeUtcTicks,
    [property: JsonRequired] string ClassName,
    [property: JsonRequired] long MonitorHandle,
    [property: JsonRequired] bool WasVisible,
    [property: JsonRequired] int ShowCommand,
    [property: JsonRequired] TaskbarWindowMutationState MutationState);
