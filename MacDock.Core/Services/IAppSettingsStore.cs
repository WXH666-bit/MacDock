using MacDock.Core.Models;

namespace MacDock.Core.Services;

public interface IAppSettingsStore
{
    AppSettings Load();

    void Save(AppSettings settings);
}
