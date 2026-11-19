using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Catalog;

/// <summary>
/// Picks an installed PHP build when a worker or scheduled task asks for a patch
/// that is no longer installed, as long as another build from the same series is.
/// </summary>
public static class PhpSeriesFallback
{
    public static string? PreferInstalled(InstallRegistryStore registry, string? versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId))
        {
            return versionId;
        }

        if (registry.IsInstalled(versionId))
        {
            return versionId;
        }

        var series = SeriesOf(versionId);
        if (series is null)
        {
            return versionId;
        }

        InstalledPackage? best = null;
        Version? bestVersion = null;
        foreach (var package in registry.List(PackageType.Php))
        {
            if (!string.Equals(SeriesOf(package.Id), series, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var version = TryParseVersion(package.Version) ?? TryParseVersion(VersionOf(package.Id));
            if (version is null)
            {
                continue;
            }

            if (bestVersion is null || version > bestVersion)
            {
                best = package;
                bestVersion = version;
            }
        }

        return best?.Id ?? versionId;
    }

    public static string? SeriesOf(string? packageId)
    {
        var version = VersionOf(packageId);
        if (version is null)
        {
            return null;
        }

        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : null;
    }

    public static string? VersionOf(string? packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return null;
        }

        const string prefix = "php-";
        if (!packageId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var version = packageId[prefix.Length..];
        return string.IsNullOrWhiteSpace(version) ? null : version;
    }

    public static Version? TryParseVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var numeric = version.Split('-', 2)[0];
        var parts = numeric.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var normalized = parts.Length switch
        {
            1 => $"{parts[0]}.0.0",
            2 => $"{parts[0]}.{parts[1]}.0",
            _ => string.Join('.', parts.Take(4))
        };

        return Version.TryParse(normalized, out var parsed) ? parsed : null;
    }
}
