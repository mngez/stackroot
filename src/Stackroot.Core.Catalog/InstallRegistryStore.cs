using System.IO;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Abstractions.DataDocuments;
using Stackroot.Core.IO.Storage;

namespace Stackroot.Core.Catalog;

public sealed class InstallRegistryStore
{
    private const int Schema = DataDocumentSchemas.Installed;
    private readonly string _dataRoot;
    private readonly IJsonFileStore _jsonFileStore;
    private InstallRegistry? _cached;
    private DateTime _cacheTimestamp;

    public InstallRegistryStore(string dataRoot, IJsonFileStore? jsonFileStore = null)
    {
        _dataRoot = dataRoot;
        _jsonFileStore = jsonFileStore ?? new Stackroot.Core.IO.Storage.JsonFileStore();
    }

    public string Path => CatalogPaths.RegistryPath(_dataRoot);

    public InstallRegistry Load()
    {
        if (_cached is not null)
        {
            var lastWrite = File.GetLastWriteTimeUtc(Path);
            if (lastWrite <= _cacheTimestamp)
            {
                return _cached;
            }
        }

        _cached = _jsonFileStore.Load(Path, EmptyRegistry);
        _cacheTimestamp = DateTime.UtcNow;
        return _cached;
    }

    public void Save(InstallRegistry registry)
    {
        var updated = registry with { SchemaVersion = Schema };
        _jsonFileStore.Save(Path, updated);
        _cached = updated;
        _cacheTimestamp = DateTime.UtcNow;
    }

    public InstalledPackage? GetById(string id)
        => Load().Packages.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public bool IsInstalled(string id)
        => GetById(id) is not null;

    public IReadOnlyList<InstalledPackage> List(PackageType? type = null)
    {
        var packages = Load().Packages;
        return type is null ? packages : packages.Where(p => p.Type == type).ToList();
    }

    public void Register(InstalledPackage package)
    {
        var registry = Load();
        registry.Packages.RemoveAll(p => string.Equals(p.Id, package.Id, StringComparison.OrdinalIgnoreCase));
        registry.Packages.Add(package);
        Save(registry);
    }

    public void Unregister(string id)
    {
        var registry = Load();
        registry.Packages.RemoveAll(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        Save(registry);
    }

    private static InstallRegistry EmptyRegistry() => new()
    {
        SchemaVersion = Schema,
        Packages = []
    };
}
