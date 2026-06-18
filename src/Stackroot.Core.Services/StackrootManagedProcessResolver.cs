using System.Diagnostics;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Nginx;
using Stackroot.Core.Settings;
using Stackroot.Core.Windows;

namespace Stackroot.Core.Services;

/// <summary>
/// Identifies process IDs that belong to Stackroot-managed services (installed package, tracked PID, or Stackroot config paths).
/// Never kills processes based on port alone.
/// </summary>
internal static class StackrootManagedProcessResolver
{
    public static bool IsServicePackageInstalled(
        ServiceDefinition definition,
        ServicePortSettings serviceSettings,
        InstallRegistryStore registry)
    {
        var packageId = serviceSettings.PackageId ?? definition.PackageId;
        return !string.IsNullOrWhiteSpace(packageId) && registry.GetById(packageId) is not null;
    }

    public static IReadOnlyList<int> ResolveOwnedListenerPids(
        ServiceId serviceId,
        ServiceDefinition definition,
        ServicePortSettings serviceSettings,
        StackrootPaths paths,
        InstallRegistryStore registry,
        int? trackedPid = null)
    {
        var owned = new HashSet<int>();

        if (trackedPid is > 0 && ServiceProcessTools.IsProcessAlive(trackedPid.Value))
        {
            owned.Add(trackedPid.Value);
        }

        if (definition.Runtime == ServiceRuntime.Library)
        {
            return owned.ToList();
        }

        var packageId = serviceSettings.PackageId ?? definition.PackageId;
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return owned.ToList();
        }

        var installed = registry.GetById(packageId);
        if (installed is null)
        {
            return owned.ToList();
        }

        if (serviceId == ServiceId.Nginx)
        {
            var masterPid = ReadNginxMasterPid(paths);
            if (masterPid is > 0 && IsOwnedListenerPid(serviceId, masterPid.Value, installed.InstallPath, paths))
            {
                owned.Add(masterPid.Value);
            }
        }

        if (serviceSettings.Port <= 0)
        {
            return owned.ToList();
        }

        foreach (var pid in ServiceProcessTools.FindPidsListeningOnPort(serviceSettings.Port))
        {
            if (IsOwnedListenerPid(serviceId, pid, installed.InstallPath, paths))
            {
                owned.Add(pid);
            }
        }

        return owned.ToList();
    }

    public static IReadOnlyList<int> ResolveOwnedPhpCgiPidsOnPort(int port, string phpInstallPath, int? trackedPid = null)
    {
        var owned = new HashSet<int>();

        if (trackedPid is > 0 && ServiceProcessTools.IsProcessAlive(trackedPid.Value))
        {
            owned.Add(trackedPid.Value);
        }

        if (port <= 0)
        {
            return owned.ToList();
        }

        foreach (var pid in ServiceProcessTools.FindPidsListeningOnPort(port))
        {
            if (IsPhpCgiFromInstall(pid, phpInstallPath))
            {
                owned.Add(pid);
            }
        }

        return owned.ToList();
    }

    public static IReadOnlyList<int> ResolvePhpCgiPidsOnPort(int port)
    {
        if (port <= 0)
        {
            return [];
        }

        return ServiceProcessTools.FindPidsListeningOnPort(port)
            .Where(pid => ServiceProcessTools.ProcessNameContains(pid, "php-cgi"))
            .Distinct()
            .ToList();
    }

    public static IReadOnlyList<int> ResolveOwnedNginxPidsOnPort(
        StackrootPaths paths,
        string nginxInstallPath,
        int port,
        int? trackedPid = null)
    {
        var owned = new HashSet<int>();

        if (trackedPid is > 0 && ServiceProcessTools.IsProcessAlive(trackedPid.Value))
        {
            owned.Add(trackedPid.Value);
        }

        var masterPid = ReadNginxMasterPid(paths);
        if (masterPid is > 0)
        {
            owned.Add(masterPid.Value);
        }

        if (port <= 0)
        {
            return owned.ToList();
        }

        foreach (var pid in ServiceProcessTools.FindPidsListeningOnPort(port))
        {
            if (IsNginxFromInstall(pid, nginxInstallPath))
            {
                owned.Add(pid);
            }
        }

        return owned.ToList();
    }

    public static void TryKillPids(IEnumerable<int> pids)
    {
        foreach (var pid in pids.Distinct())
        {
            if (pid > 0)
            {
                ProcessKiller.TryKill(pid);
            }
        }
    }

    public static void TryKillOwnedListenersOnPort(int port, Func<int, bool> isOwned)
    {
        ServiceProcessTools.TryKillListenersOnPort(port, isOwned);
    }

    private static bool IsOwnedListenerPid(
        ServiceId serviceId,
        int pid,
        string installPath,
        StackrootPaths paths)
    {
        _ = paths;
        if (!ServiceProcessTools.IsExecutableUnderInstallPath(pid, installPath))
        {
            return false;
        }

        return serviceId switch
        {
            ServiceId.Nginx => IsNginxFromInstall(pid, installPath),
            ServiceId.Redis => ServiceProcessTools.ProcessNameContains(pid, "redis"),
            ServiceId.Memcached => ServiceProcessTools.ProcessNameContains(pid, "memcached"),
            ServiceId.Mysql or ServiceId.Mariadb => ServiceProcessTools.ProcessNameContains(pid, "mysqld"),
            ServiceId.Postgresql => ServiceProcessTools.ProcessNameContains(pid, "postgres"),
            ServiceId.Mongodb => ServiceProcessTools.ProcessNameContains(pid, "mongod"),
            _ => true
        };
    }

    private static bool IsPhpCgiFromInstall(int pid, string phpInstallPath) =>
        ServiceProcessTools.ProcessNameContains(pid, "php-cgi") &&
        ServiceProcessTools.IsExecutableUnderInstallPath(pid, phpInstallPath);

    private static bool IsNginxFromInstall(int pid, string nginxInstallPath) =>
        ServiceProcessTools.ProcessNameContains(pid, "nginx") &&
        ServiceProcessTools.IsExecutableUnderInstallPath(pid, nginxInstallPath);

    private static int? ReadNginxMasterPid(StackrootPaths paths)
    {
        var pidFile = Path.Combine(NginxRuntime.nginxPrefix(paths), "logs", "nginx.pid");
        if (!File.Exists(pidFile))
        {
            return null;
        }

        var text = File.ReadAllText(pidFile).Trim();
        return int.TryParse(text, out var pid) && pid > 0 ? pid : null;
    }
}
