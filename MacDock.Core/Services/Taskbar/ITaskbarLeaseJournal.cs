namespace MacDock.Core.Services.Taskbar;

public interface ITaskbarLeaseJournal
{
    string FilePath { get; }

    TaskbarLeaseDocument? Read();

    void Write(TaskbarLeaseDocument document);

    void Delete();
}
