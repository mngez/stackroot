using Stackroot.Core.Abstractions;
using Stackroot.Core.IO.Storage;

namespace Stackroot.Core.Settings;

public sealed class AppSettingsStore
{
    private readonly IJsonFileStore _jsonFileStore;

    public AppSettingsStore(IJsonFileStore? jsonFileStore = null)
    {
        _jsonFileStore = jsonFileStore ?? new JsonFileStore();
    }

    public AppSettings Load(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "settings.json");
        return _jsonFileStore.Load(path, () => new AppSettings());
    }

    public void Save(string dataRoot, AppSettings settings)
    {
        var path = Path.Combine(dataRoot, "settings.json");
        _jsonFileStore.Save(path, settings);
    }
}
