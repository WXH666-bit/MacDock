using MacDock.Core.Services;

namespace MacDock.Core.Services.Taskbar;

public sealed class TaskbarLeaseJournal : ITaskbarLeaseJournal
{
    private const string PrimaryTaskbarClassName = "Shell_TrayWnd";
    private const int MinimumShowCommand = 0;
    private const int MaximumShowCommand = 11;

    private readonly AtomicJsonFile<TaskbarLeaseDocument> _file;

    public TaskbarLeaseJournal(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("A lease journal path is required.", nameof(filePath));

        FilePath = filePath;
        _file = new AtomicJsonFile<TaskbarLeaseDocument>(filePath);
    }

    public string FilePath { get; }

    public TaskbarLeaseDocument? Read()
    {
        TaskbarLeaseDocument document;
        try
        {
            document = _file.Read();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        Validate(document);
        return document;
    }

    public void Write(TaskbarLeaseDocument document)
    {
        Validate(document);
        _file.Write(document);
    }

    public void Delete()
    {
        File.Delete(FilePath);
    }

    private static void Validate(TaskbarLeaseDocument? document)
    {
        if (document is null)
            throw new InvalidDataException("The taskbar lease document must not be null.");

        if (document.SchemaVersion != TaskbarLeaseDocument.CurrentSchemaVersion)
            throw new InvalidDataException("The taskbar lease schema version is unsupported.");

        if (!Guid.TryParse(document.LeaseId, out var leaseId) || leaseId == Guid.Empty)
            throw new InvalidDataException("The taskbar lease ID must be a non-empty GUID.");

        if (document.OwnerProcessId <= 0)
            throw new InvalidDataException("The taskbar lease owner process ID must be positive.");

        if (document.OwnerProcessStartTimeUtcTicks <= 0)
            throw new InvalidDataException("The taskbar lease owner start time must be positive.");

        if (document.WatchdogProcessId is <= 0)
            throw new InvalidDataException("The watchdog process ID must be positive when present.");

        if (!Enum.IsDefined(document.Status))
            throw new InvalidDataException("The taskbar lease status is unsupported.");

        if (document.Generation <= 0)
            throw new InvalidDataException("The taskbar lease generation must be positive.");

        if (document.UpdatedAtUtc == default)
            throw new InvalidDataException("The taskbar lease update time is required.");

        if (document.Windows is not { Count: > 0 })
            throw new InvalidDataException("The taskbar lease must contain at least one window snapshot.");

        var identities = new HashSet<(long Handle, uint ProcessId, long ProcessStartTimeUtcTicks)>();
        foreach (var snapshot in document.Windows)
        {
            ValidateSnapshot(snapshot);
            if (!identities.Add(
                    (snapshot.Handle, snapshot.ProcessId, snapshot.ProcessStartTimeUtcTicks)))
            {
                throw new InvalidDataException(
                    "The taskbar lease contains a duplicate window identity.");
            }
        }
    }

    private static void ValidateSnapshot(TaskbarWindowSnapshot? snapshot)
    {
        if (snapshot is null)
            throw new InvalidDataException("The taskbar lease contains a null window snapshot.");

        if (snapshot.Handle <= 0)
            throw new InvalidDataException("The taskbar window handle must be positive.");

        if (snapshot.ProcessId == 0)
            throw new InvalidDataException("The taskbar window process ID must be positive.");

        if (snapshot.ProcessStartTimeUtcTicks <= 0)
            throw new InvalidDataException("The taskbar window process start time must be positive.");

        if (!string.Equals(snapshot.ClassName, PrimaryTaskbarClassName, StringComparison.Ordinal))
            throw new InvalidDataException("The taskbar window class is outside the safe scope.");

        if (snapshot.MonitorHandle <= 0)
            throw new InvalidDataException("The taskbar window monitor handle must be positive.");

        if (snapshot.ShowCommand is < MinimumShowCommand or > MaximumShowCommand)
            throw new InvalidDataException("The taskbar window show command is invalid.");

        if (snapshot.WasVisible && snapshot.ShowCommand == MinimumShowCommand)
            throw new InvalidDataException(
                "A visible taskbar window must have a recoverable show command.");

        if (!Enum.IsDefined(snapshot.MutationState))
            throw new InvalidDataException("The taskbar window mutation state is unsupported.");

        if (snapshot.MutationState != TaskbarWindowMutationState.Unchanged
            && !snapshot.WasVisible)
        {
            throw new InvalidDataException(
                "A taskbar window changed by the lease must have been visible in its snapshot.");
        }
    }
}
