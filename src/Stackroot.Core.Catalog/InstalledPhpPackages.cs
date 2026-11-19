using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Catalog;

/// <summary>
/// Installed PHP builds the user can still select and remove, including copies
/// whose catalog entry was retired.
/// </summary>
public static class InstalledPhpPackages
{
    public static IReadOnlyList<PackageEntry> List(PackageCatalogStore catalog, InstallRegistryStore registry)
    {
        var entries = new List<PackageEntry>();
        foreach (var installed in registry.List(PackageType.Php))
        {
            entries.Add(catalog.GetById(installed.Id) ?? Synthesize(installed));
        }

        return entries
            .OrderByDescending(entry => PhpSeriesFallback.TryParseVersion(entry.Version) ?? new Version(0, 0))
            .ThenByDescending(entry => entry.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static PackageEntry? ResolveInstalled(PackageCatalogStore catalog, InstallRegistryStore registry, string versionId)
    {
        var installed = registry.GetById(versionId);
        if (installed is null || installed.Type != PackageType.Php)
        {
            return null;
        }

        return catalog.GetById(installed.Id) ?? Synthesize(installed);
    }

    public static PackageEntry Synthesize(InstalledPackage installed)
    {
        var version = string.IsNullOrWhiteSpace(installed.Version)
            ? PhpSeriesFallback.VersionOf(installed.Id) ?? installed.Id
            : installed.Version;

        return new PackageEntry
        {
            Id = installed.Id,
            Type = PackageType.Php,
            Version = version,
            Series = PhpSeriesFallback.SeriesOf(installed.Id),
            Platform = PlatformType.WinX64,
            Label = $"PHP {version}",
            Description = "Installed copy kept after it left the catalog. You can still select, configure, and remove it.",
            InstallDir = installed.InstallPath,
            Executable = "php.exe"
        };
    }
}
