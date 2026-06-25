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

    /// <summary>Number of php-cgi workers per PHP version, clamped to a sane range.</summary>
    public static int ResolvePoolSize(AppSettings settings)
    {
        var size = settings.Php.FpmPoolSize;
        if (size < 1)
        {
            return 1;
        }

        return size > 8 ? 8 : size;
    }

    private static int ResolveBasePort(AppSettings settings) =>
        settings.Php.FpmPort <= 0 ? 9000 : settings.Php.FpmPort;

    /// <summary>
    /// Worker 0's port for a version — also the representative/anchor port used by
    /// admin tools and dashboard rows. Each version owns a contiguous block of
    /// <see cref="ResolvePoolSize"/> ports.
    /// </summary>
    public static int ResolvePlannedPort(AppSettings settings, int index) =>
        ResolveBasePort(settings) + index * ResolvePoolSize(settings);

    /// <summary>All worker ports for the version at <paramref name="index"/>.</summary>
    public static IReadOnlyList<int> ResolveWorkerPorts(AppSettings settings, int index)
    {
        var basePort = ResolvePlannedPort(settings, index);
        var poolSize = ResolvePoolSize(settings);
        var ports = new int[poolSize];
        for (var i = 0; i < poolSize; i++)
        {
            ports[i] = basePort + i;
        }

        return ports;
    }

    public static IReadOnlyList<int> ResolveWorkerPortsForVersion(
        AppSettings settings,
        InstallRegistryStore registry,
        string versionId)
    {
        var index = ResolveVersionIndex(registry, versionId);
        return index is null ? [] : ResolveWorkerPorts(settings, index.Value);
    }

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
