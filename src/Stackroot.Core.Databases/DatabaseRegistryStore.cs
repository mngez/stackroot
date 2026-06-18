using Stackroot.Core.Databases.Models;
using Stackroot.Core.IO;
using Stackroot.Core.IO.Storage;
using StorageJsonFileStore = Stackroot.Core.IO.Storage.JsonFileStore;

namespace Stackroot.Core.Databases;

public sealed class DatabaseRegistryStore
{
    private const int Schema = 1;
    private readonly string _dataRoot;
    private readonly IJsonFileStore _jsonFileStore;

    public DatabaseRegistryStore(string dataRoot, IJsonFileStore? jsonFileStore = null)
    {
        _dataRoot = dataRoot;
        _jsonFileStore = jsonFileStore ?? new StorageJsonFileStore();
    }

    public string Path => StackrootPathResolver.DatabasesRegistryPath(_dataRoot);

    public DatabasesRegistry Load()
    {
        return _jsonFileStore.Load(Path, () => new DatabasesRegistry
        {
            SchemaVersion = Schema,
            Databases = []
        });
    }

    public void Save(DatabasesRegistry registry)
    {
        _jsonFileStore.Save(Path, registry with { SchemaVersion = Schema });
    }
}
