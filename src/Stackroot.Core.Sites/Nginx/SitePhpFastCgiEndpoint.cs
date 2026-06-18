using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;

namespace Stackroot.Core.Sites.Nginx;

public static class SitePhpFastCgiEndpoint
{
    public static string Resolve(AppSettings settings, InstallRegistryStore registry, string? versionId)
    {
        var host = string.IsNullOrWhiteSpace(settings.Php.FpmHost) ? "127.0.0.1" : settings.Php.FpmHost.Trim();
        var basePort = settings.Php.FpmPort <= 0 ? 9000 : settings.Php.FpmPort;

        if (string.IsNullOrWhiteSpace(versionId))
        {
            versionId = settings.Php.ActiveVersionId;
        }

        if (string.IsNullOrWhiteSpace(versionId))
        {
            return $"{host}:{basePort}";
        }

        var ordered = registry.List(PackageType.Php)
            .OrderByDescending(package => package.Id, StringComparer.OrdinalIgnoreCase)
            .Select(package => package.Id)
            .ToList();

        for (var index = 0; index < ordered.Count; index++)
        {
            if (string.Equals(ordered[index], versionId, StringComparison.OrdinalIgnoreCase))
            {
                return $"{host}:{basePort + index}";
            }
        }

        return $"{host}:{basePort}";
    }

    public static string? ResolvePhpRcPath(StackrootPaths paths, AppSettings settings, string? versionId)
    {
        var effectiveVersionId = string.IsNullOrWhiteSpace(versionId)
            ? settings.Php.ActiveVersionId
            : versionId.Trim();
        if (string.IsNullOrWhiteSpace(effectiveVersionId))
        {
            return null;
        }

        var iniPath = PhpConfigPaths.ResolveExistingDefaultIniPath(paths.ConfigRoot, effectiveVersionId);
        return iniPath is null ? null : PhpConfigPaths.ToNginxPhpRc(iniPath);
    }
}
