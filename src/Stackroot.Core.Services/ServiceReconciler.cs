using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Settings;

namespace Stackroot.Core.Services;

public static class ServiceReconciler
{
    public static bool Reconcile(AppSettings settings, InstallRegistryStore registry, IReadOnlyList<string>? databaseEngines = null)
    {
        var changed = ReconcileServicePackages(settings, registry);
        changed = ReconcileSqlEngineState(settings, registry, databaseEngines) || changed;
        return changed;
    }

    public static bool ReconcileServicePackages(AppSettings settings, InstallRegistryStore registry)
    {
        var changed = false;

        foreach (var definition in SettingsDefaults.ServiceDefinitions)
        {
            if (string.IsNullOrWhiteSpace(definition.PackageId))
            {
                continue;
            }

            if (!settings.Services.TryGetValue(definition.Id, out var serviceSettings))
            {
                continue;
            }

            var currentId = serviceSettings.PackageId ?? definition.PackageId;
            if (!string.IsNullOrWhiteSpace(currentId) && registry.IsInstalled(currentId))
            {
                continue;
            }

            var packageType = MapPackageType(definition.Id);
            if (packageType is null)
            {
                continue;
            }

            var installed = registry.List(packageType.Value);
            if (installed.Count == 0)
            {
                continue;
            }

            var nextId = PickPreferredPackage(
                installed.Select(package => package.Id).ToArray(),
                currentId);

            if (!string.Equals(serviceSettings.PackageId, nextId, StringComparison.OrdinalIgnoreCase))
            {
                settings.Services[definition.Id] = serviceSettings with { PackageId = nextId };
                changed = true;
            }
        }

        return changed;
    }

    public static bool ReconcileSqlEngineState(
        AppSettings settings,
        InstallRegistryStore registry,
        IReadOnlyList<string>? databaseEngines = null)
    {
        var changed = ReconcileServicePackages(settings, registry);
        var engines = databaseEngines ?? [];

        var active = InferActiveSqlEngine(settings, registry, engines);
        if (active is not null)
        {
            changed = EnsureActiveSqlEngineLinked(settings, registry, active.Value) || changed;
        }

        changed = MigrateSqlEngineConflict(settings) || changed;
        return changed;
    }

    private static bool EnsureActiveSqlEngineLinked(
        AppSettings settings,
        InstallRegistryStore registry,
        ServiceId active)
    {
        var changed = false;
        var serviceSettings = settings.Services[active];
        var packageType = active == ServiceId.Mysql ? PackageType.Mysql : PackageType.Mariadb;
        var installed = registry.List(packageType);
        if (installed.Count == 0)
        {
            return false;
        }

        var linked = !string.IsNullOrWhiteSpace(serviceSettings.PackageId)
            && registry.IsInstalled(serviceSettings.PackageId);
        if (!linked)
        {
            var nextId = PickPreferredPackage(
                installed.Select(package => package.Id).ToArray(),
                serviceSettings.PackageId);
            settings.Services[active] = serviceSettings with { PackageId = nextId };
            changed = true;
            serviceSettings = settings.Services[active];
        }

        if (!serviceSettings.Enabled)
        {
            settings.Services[active] = serviceSettings with { Enabled = true };
            changed = true;
        }

        var sqlEngine = active == ServiceId.Mysql ? SqlEngine.Mysql : SqlEngine.Mariadb;
        if (settings.Databases.ActiveSqlEngine != sqlEngine)
        {
            settings.Databases = settings.Databases with { ActiveSqlEngine = sqlEngine };
            changed = true;
        }

        return changed;
    }

    private static ServiceId? InferActiveSqlEngine(
        AppSettings settings,
        InstallRegistryStore registry,
        IReadOnlyList<string> databaseEngines)
    {
        if (settings.Databases.ActiveSqlEngine == SqlEngine.Mysql)
        {
            return ServiceId.Mysql;
        }

        if (settings.Databases.ActiveSqlEngine == SqlEngine.Mariadb)
        {
            return ServiceId.Mariadb;
        }

        if (databaseEngines.Contains("mysql", StringComparer.OrdinalIgnoreCase))
        {
            return ServiceId.Mysql;
        }

        if (databaseEngines.Contains("mariadb", StringComparer.OrdinalIgnoreCase))
        {
            return ServiceId.Mariadb;
        }

        var mysqlInstalled = registry.List(PackageType.Mysql).Count > 0;
        var mariadbInstalled = registry.List(PackageType.Mariadb).Count > 0;
        if (mysqlInstalled && !mariadbInstalled)
        {
            return ServiceId.Mysql;
        }

        if (mariadbInstalled && !mysqlInstalled)
        {
            return ServiceId.Mariadb;
        }

        if (settings.Services.TryGetValue(ServiceId.Mysql, out var mysql) &&
            settings.Services.TryGetValue(ServiceId.Mariadb, out var mariadb))
        {
            if (mysql.Enabled && !mariadb.Enabled)
            {
                return ServiceId.Mysql;
            }

            if (mariadb.Enabled && !mysql.Enabled)
            {
                return ServiceId.Mariadb;
            }
        }

        return null;
    }

    private static bool MigrateSqlEngineConflict(AppSettings settings)
    {
        if (!settings.Services.TryGetValue(ServiceId.Mysql, out var mysql) ||
            !settings.Services.TryGetValue(ServiceId.Mariadb, out var mariadb))
        {
            return false;
        }

        if (!mysql.Enabled || !mariadb.Enabled)
        {
            return false;
        }

        if (mysql.Port != mariadb.Port)
        {
            return false;
        }

        var keepMaria = settings.Databases.ActiveSqlEngine == SqlEngine.Mariadb;
        if (keepMaria)
        {
            settings.Services[ServiceId.Mysql] = mysql with { Enabled = false };
        }
        else
        {
            settings.Services[ServiceId.Mariadb] = mariadb with { Enabled = false };
        }

        return true;
    }

    private static string PickPreferredPackage(IReadOnlyList<string> installedIds, string? previousId)
    {
        if (!string.IsNullOrWhiteSpace(previousId))
        {
            var prefix = System.Text.RegularExpressions.Regex.Replace(previousId, @"\d[\d.]*$", string.Empty);
            var sameLine = installedIds.FirstOrDefault(id => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(sameLine))
            {
                return sameLine;
            }
        }

        return installedIds[0];
    }

    private static PackageType? MapPackageType(ServiceId serviceId) =>
        serviceId switch
        {
            ServiceId.Nginx => PackageType.Nginx,
            ServiceId.Redis => PackageType.Redis,
            ServiceId.Memcached => PackageType.Memcached,
            ServiceId.Imagemagick => PackageType.Imagemagick,
            ServiceId.Gdlibs => PackageType.Gdlibs,
            ServiceId.Mysql => PackageType.Mysql,
            ServiceId.Mariadb => PackageType.Mariadb,
            ServiceId.Postgresql => PackageType.Postgresql,
            ServiceId.Mongodb => PackageType.Mongodb,
            _ => null
        };
}
