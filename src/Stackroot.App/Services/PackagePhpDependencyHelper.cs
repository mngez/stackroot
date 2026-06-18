using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Services;

namespace Stackroot.App.Services;

public sealed record PhpInstallOption(
    string Id,
    string Label,
    bool IsInstalled,
    bool Compatible,
    string? IncompatibleReason);

public static class PackagePhpDependencyHelper
{
    public static RequiresPhp ResolveRequirement(PackageEntry package, RequiresPhp? fallback = null) =>
        package.RequiresPhp ?? fallback ?? new RequiresPhp { Min = "7.4.0" };

    public static string FormatRequirement(PackageEntry package, RequiresPhp? fallback = null) =>
        AdminToolPhpResolver.FormatPhpRequirement(ResolveRequirement(package, fallback));

    public static IReadOnlyList<PhpInstallOption> ListCompatiblePhpOptions(
        PackageEntry package,
        PackageCatalogStore catalog,
        InstallRegistryStore registry,
        RequiresPhp? fallback = null)
    {
        var requirement = ResolveRequirement(package, fallback);
        return catalog.List(PackageType.Php)
            .Select(php =>
            {
                var compatible = AdminToolPhpResolver.IsPhpCompatible(php.Id, requirement);
                return new PhpInstallOption(
                    php.Id,
                    php.Label,
                    registry.IsInstalled(php.Id),
                    compatible,
                    compatible ? null : $"Requires {AdminToolPhpResolver.FormatPhpRequirement(requirement)}");
            })
            .Where(option => option.Compatible)
            .OrderByDescending(option => option.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<PackageEntry> ListCompatiblePhpCatalogEntries(
        PackageEntry package,
        PackageCatalogStore catalog,
        RequiresPhp? fallback = null)
    {
        var requirement = ResolveRequirement(package, fallback);
        return catalog.List(PackageType.Php)
            .Where(php => AdminToolPhpResolver.IsPhpCompatible(php.Id, requirement))
            .OrderByDescending(php => php.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
