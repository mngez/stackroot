using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Settings;

namespace Stackroot.Core.Services;

public static class PhpCgiPlanner
{
    public static IReadOnlyList<string> OrderInstalledVersionIds(InstallRegistryStore registry) =>
        registry.List(PackageType.Php)
            .OrderByDescending(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .Select(p => p.Id)
            .ToList();

    public static int ResolvePlannedPort(AppSettings settings, int index) =>
        (settings.Php.FpmPort <= 0 ? 9000 : settings.Php.FpmPort) + index;

    public static int? ResolveVersionIndex(InstallRegistryStore registry, string versionId)
    {
        var ordered = OrderInstalledVersionIds(registry);
        for (var i = 0; i < ordered.Count; i++)
        {
            if (string.Equals(ordered[i], versionId, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return null;
    }

    public static int? ResolvePlannedPortForVersion(AppSettings settings, InstallRegistryStore registry, string versionId)
    {
        var index = ResolveVersionIndex(registry, versionId);
        return index is null ? null : ResolvePlannedPort(settings, index.Value);
    }

    public static IReadOnlyList<string> ResolveRequiredVersionIds(
        AppSettings settings,
        InstallRegistryStore registry,
        IEnumerable<string>? extraVersionIds = null,
        PackageCatalogStore? catalog = null)
    {
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(settings.Php.ActiveVersionId) &&
            registry.GetById(settings.Php.ActiveVersionId) is not null)
        {
            required.Add(settings.Php.ActiveVersionId);
        }

        if (settings.Phpmyadmin.Enabled)
        {
            var packageId = string.IsNullOrWhiteSpace(settings.Phpmyadmin.PackageId)
                ? SettingsDefaults.DefaultPhpMyAdminPackageId
                : settings.Phpmyadmin.PackageId.Trim();
            var requirement = AdminToolPhpResolver.RequirementForPhpMyAdmin(catalog, packageId);
            var phpVersionId = AdminToolPhpResolver.ResolveVersionId(
                settings.Phpmyadmin.PhpVersionId,
                requirement,
                settings,
                registry);
            if (!string.IsNullOrWhiteSpace(phpVersionId))
            {
                required.Add(phpVersionId);
            }
        }

        if (settings.Phpredisadmin.Enabled)
        {
            var packageId = string.IsNullOrWhiteSpace(settings.Phpredisadmin.PackageId)
                ? SettingsDefaults.DefaultPhpRedisAdminPackageId
                : settings.Phpredisadmin.PackageId.Trim();
            var requirement = AdminToolPhpResolver.RequirementForPhpRedisAdmin(catalog, packageId);
            var phpVersionId = AdminToolPhpResolver.ResolveVersionId(
                settings.Phpredisadmin.PhpVersionId,
                requirement,
                settings,
                registry);
            if (!string.IsNullOrWhiteSpace(phpVersionId))
            {
                required.Add(phpVersionId);
            }
        }

        foreach (var versionId in extraVersionIds ?? [])
        {
            if (!string.IsNullOrWhiteSpace(versionId) && registry.GetById(versionId) is not null)
            {
                required.Add(versionId);
            }
        }

        return OrderInstalledVersionIds(registry)
            .Where(required.Contains)
            .ToList();
    }
}
