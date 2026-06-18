using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Catalog;

public static class CatalogUrlRepair
{
    public const string BrokenNginxUrl = "https://packages.stackroot.dev/nginx/1.26.2/win-x64.zip";
    public const string FixedNginxUrl = "https://nginx.org/download/nginx-1.26.2.zip";

    public static bool RepairKnownCatalogUrls(string resourcesRoot)
    {
        var catalogPath = CatalogPaths.CatalogPath(resourcesRoot);
        if (!File.Exists(catalogPath))
        {
            return false;
        }

        var text = File.ReadAllText(catalogPath);
        var changed = false;
        if (text.Contains(BrokenNginxUrl, StringComparison.OrdinalIgnoreCase))
        {
            text = text.Replace(BrokenNginxUrl, FixedNginxUrl, StringComparison.OrdinalIgnoreCase);
            changed = true;
        }

        PackageCatalog? catalog;
        try
        {
            catalog = System.Text.Json.JsonSerializer.Deserialize<PackageCatalog>(
                text,
                Stackroot.Core.IO.JsonSerializerConfig.Default);
        }
        catch
        {
            if (changed)
            {
                File.WriteAllText(catalogPath, text);
            }

            return changed;
        }

        if (catalog?.Packages is null)
        {
            if (changed)
            {
                File.WriteAllText(catalogPath, text);
            }

            return changed;
        }

        var repairedPackages = catalog.Packages
            .Select(RepairPackageEntry)
            .ToList();
        if (repairedPackages.Where((entry, index) => !Equals(entry, catalog.Packages[index])).Any())
        {
            catalog = catalog with { Packages = repairedPackages };
            changed = true;
        }

        var deduped = catalog.Packages
            .GroupBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (deduped.Count != catalog.Packages.Count)
        {
            catalog = catalog with { Packages = deduped };
            changed = true;
        }

        if (!changed)
        {
            return false;
        }

        var store = new PackageCatalogStore(resourcesRoot);
        store.Save(catalog);
        return true;
    }

    private static PackageEntry RepairPackageEntry(PackageEntry package)
    {
        if (!package.Id.Equals("gd-libs-8.4", StringComparison.OrdinalIgnoreCase))
        {
            return package;
        }

        var remoteUrl = package.Remote?.Url ?? string.Empty;
        if (!remoteUrl.Contains("php-8.4", StringComparison.OrdinalIgnoreCase))
        {
            return package;
        }

        return package with { Remote = null };
    }
}
