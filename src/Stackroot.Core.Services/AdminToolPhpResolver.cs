using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Settings;

namespace Stackroot.Core.Services;

/// <summary>
/// Resolves which installed PHP version an admin tool (phpMyAdmin, phpRedisAdmin) should use.
/// Single source of truth for nginx FastCGI ports and php-cgi lifecycle.
/// </summary>
public static class AdminToolPhpResolver
{
    public static readonly RequiresPhp DefaultPhpMyAdminRequirement = new() { Min = "7.2.0" };
    public static readonly RequiresPhp DefaultPhpRedisAdminRequirement = new() { Min = "7.4.0" };

    private static readonly string[] PreferredPhpLines = ["8.5", "8.4", "8.3", "8.2", "8.1", "8.0", "7.4"];

    public static RequiresPhp RequirementForPackage(
        PackageCatalogStore? catalog,
        string packageId,
        RequiresPhp fallback)
    {
        var entry = catalog?.GetById(packageId);
        return entry?.RequiresPhp ?? fallback;
    }

    public static RequiresPhp RequirementForPhpMyAdmin(PackageCatalogStore? catalog, string packageId) =>
        RequirementForPackage(catalog, packageId, DefaultPhpMyAdminRequirement);

    public static RequiresPhp RequirementForPhpRedisAdmin(PackageCatalogStore? catalog, string packageId) =>
        RequirementForPackage(catalog, packageId, DefaultPhpRedisAdminRequirement);

    public static string? ResolveVersionId(
        string? configuredVersionId,
        RequiresPhp requirement,
        AppSettings settings,
        InstallRegistryStore registry)
    {
        string? Accept(string? id)
        {
            if (string.IsNullOrWhiteSpace(id) || registry.GetById(id) is null)
            {
                return null;
            }

            return IsPhpCompatible(id, requirement) ? id : null;
        }

        if (!string.IsNullOrWhiteSpace(configuredVersionId))
        {
            var chosen = Accept(configuredVersionId);
            if (chosen is not null)
            {
                return chosen;
            }
        }

        var activeVersion = Accept(settings.Php.ActiveVersionId);
        if (activeVersion is not null)
        {
            return activeVersion;
        }

        var installedPhp = registry.List(PackageType.Php);
        foreach (var line in PreferredPhpLines)
        {
            var match = installedPhp.FirstOrDefault(p =>
                ParsePhpVersionFromPackageId(p.Id)?.StartsWith($"{line}.", StringComparison.Ordinal) == true);
            var id = Accept(match?.Id);
            if (id is not null)
            {
                return id;
            }
        }

        foreach (var pkg in installedPhp.OrderByDescending(p => p.Version, StringComparer.OrdinalIgnoreCase))
        {
            var id = Accept(pkg.Id);
            if (id is not null)
            {
                return id;
            }
        }

        return null;
    }

    public static bool IsPhpCompatible(string phpVersionId, RequiresPhp requirement)
    {
        var version = ParsePhpVersionFromPackageId(phpVersionId);
        if (string.IsNullOrWhiteSpace(version) || !Version.TryParse(NormalizeVersion(version), out var parsed))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(requirement.Min) &&
            Version.TryParse(NormalizeVersion(requirement.Min), out var min) &&
            parsed < min)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(requirement.MaxExclusive) &&
            Version.TryParse(NormalizeVersion(requirement.MaxExclusive), out var maxExclusive) &&
            parsed >= maxExclusive)
        {
            return false;
        }

        return true;
    }

    public static string FormatPhpRequirement(RequiresPhp requirement)
    {
        if (!string.IsNullOrWhiteSpace(requirement.Min) && !string.IsNullOrWhiteSpace(requirement.MaxExclusive))
        {
            return $"PHP {requirement.Min} – {requirement.MaxExclusive}";
        }

        if (!string.IsNullOrWhiteSpace(requirement.Min))
        {
            return $"PHP {requirement.Min}+";
        }

        return "compatible PHP";
    }

    public static string? ParsePhpVersionFromPackageId(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return null;
        }

        if (!packageId.StartsWith("php-", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var versionPart = packageId["php-".Length..];
        return string.IsNullOrWhiteSpace(versionPart) ? null : versionPart;
    }

    private static string NormalizeVersion(string version)
    {
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            1 => $"{parts[0]}.0.0",
            2 => $"{parts[0]}.{parts[1]}.0",
            _ => version
        };
    }
}
