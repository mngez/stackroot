using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;

namespace Stackroot.Core.Sites.Nginx;

public static class SitePhpFastCgiEndpoint
{
    /// <summary>
    /// Resolves the <c>fastcgi_pass</c> target for a site. Returns the name of the nginx
    /// upstream that fronts the version's php-cgi worker pool (defined in php-upstreams.conf),
    /// so nginx load-balances across workers and fails over instantly when one recycles.
    /// Falls back to a literal host:port only when no installed PHP version can be resolved.
    /// </summary>
    public static string Resolve(AppSettings settings, InstallRegistryStore registry, string? versionId)
    {
        var host = string.IsNullOrWhiteSpace(settings.Php.FpmHost) ? "127.0.0.1" : settings.Php.FpmHost.Trim();
        var basePort = settings.Php.FpmPort <= 0 ? 9000 : settings.Php.FpmPort;

        if (string.IsNullOrWhiteSpace(versionId))
        {
            versionId = settings.Php.ActiveVersionId;
        }

        if (!string.IsNullOrWhiteSpace(versionId))
        {
            var canonicalId = registry.List(PackageType.Php)
                .OrderByDescending(package => package.Id, StringComparer.OrdinalIgnoreCase)
                .Select(package => package.Id)
                .FirstOrDefault(id => string.Equals(id, versionId, StringComparison.OrdinalIgnoreCase));

            if (canonicalId is not null)
            {
                return PhpFastCgiNaming.UpstreamName(canonicalId);
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
