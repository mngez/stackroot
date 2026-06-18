using Stackroot.Core.Abstractions;
using Stackroot.Core.IO.Storage;

namespace Stackroot.Core.Catalog;

public sealed class PackageCatalogStore
{
    private const int Schema = 1;
    private readonly string _resourcesRoot;
    private readonly IJsonFileStore _jsonFileStore;

    public PackageCatalogStore(string resourcesRoot, IJsonFileStore? jsonFileStore = null)
    {
        _resourcesRoot = resourcesRoot;
        _jsonFileStore = jsonFileStore ?? new JsonFileStore();
    }

    public string Path => CatalogPaths.CatalogPath(_resourcesRoot);

    public PackageCatalog Load()
    {
        return _jsonFileStore.Load(Path, () => new PackageCatalog
        {
            SchemaVersion = Schema,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
            Packages = []
        });
    }

    public void Save(PackageCatalog catalog)
    {
        _jsonFileStore.Save(Path, catalog with
        {
            SchemaVersion = Schema,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
        });
    }

    public IReadOnlyList<PackageEntry> List(PackageType? type = null)
    {
        var packages = Load().Packages
            .GroupBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        return type is null ? packages : packages.Where(p => p.Type == type).ToList();
    }

    public PackageEntry? GetById(string id)
        => Load().Packages.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
