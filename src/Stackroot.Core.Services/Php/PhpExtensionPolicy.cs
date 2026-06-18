using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Settings;

namespace Stackroot.Core.Services.Php;

public static class PhpExtensionPolicy
{
    private static readonly HashSet<string> DefaultOnExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "bcmath", "curl", "ctype", "dom", "exif", "fileinfo", "filter", "gd", "hash", "iconv", "intl", "json",
        "libxml", "mbstring", "mysqli", "openssl", "pcre", "pdo", "pdo_mysql", "pdo_sqlite", "random", "reflection", "session", "simplexml",
        "soap", "sockets", "sodium", "spl", "sqlite3", "standard", "tokenizer", "xml", "xmlreader", "xmlwriter",
        "zip", "zlib", "opcache"
    };

    private static readonly Dictionary<string, ServiceId[]> BundledServiceAny = new(StringComparer.OrdinalIgnoreCase)
    {
    };

    public static bool DefaultExtensionPreference(string extensionId, PhpExtensionsManifest? manifest, InstallRegistryStore registry, AppSettings settings)
    {
        var entry = manifest?.Extensions.FirstOrDefault(e => string.Equals(e.Id, extensionId, StringComparison.OrdinalIgnoreCase));
        if (entry?.RequiresAnyService is { Count: > 0 } services)
        {
            return services.Any(serviceId => IsServiceInstalled(registry, settings, serviceId));
        }

        if (BundledServiceAny.TryGetValue(extensionId, out var bundledServices))
        {
            return bundledServices.Any(serviceId => IsServiceInstalled(registry, settings, serviceId));
        }

        return DefaultOnExtensions.Contains(extensionId);
    }

    public static bool IsServiceInstalled(InstallRegistryStore registry, AppSettings settings, ServiceId serviceId)
    {
        var packageId = settings.Services.TryGetValue(serviceId, out var serviceSettings)
            ? serviceSettings.PackageId
            : SettingsDefaults.DefaultServices()[serviceId].PackageId;

        return !string.IsNullOrWhiteSpace(packageId) && registry.IsInstalled(packageId);
    }

    public static string? ExtensionBlockedReason(
        string extensionId,
        PhpExtensionManifestEntry? entry,
        InstallRegistryStore registry,
        AppSettings settings,
        string extDir)
    {
        if (entry?.WindowsSupported == false)
        {
            return "Not supported on Windows";
        }

        if (!ExtensionDllExists(extDir, extensionId))
        {
            return "Extension DLL not installed";
        }

        var services = entry?.RequiresAnyService ?? (BundledServiceAny.TryGetValue(extensionId, out var bundled) ? bundled.ToList() : null);
        if (services is { Count: > 0 }
            && !services.Any(serviceId => IsServiceInstalled(registry, settings, serviceId)))
        {
            var names = string.Join(", ", services.Select(s => SettingsDefaults.ServiceDefinitions.First(d => d.Id == s).Name));
            return $"Requires {names}";
        }

        return null;
    }

    public static bool CanLoadExtension(string installPath, string extensionId)
    {
        var phpRoot = ResolvePackageRoot(installPath);
        var extDir = Path.Combine(phpRoot, "ext");

        if (string.Equals(extensionId, "opcache", StringComparison.OrdinalIgnoreCase))
        {
            return File.Exists(Path.Combine(phpRoot, "php_opcache.dll"))
                   || File.Exists(Path.Combine(extDir, "php_opcache.dll"));
        }

        return ExtensionDllExists(extDir, extensionId);
    }

    public static bool ExtensionDllExists(string extDir, string extensionId)
    {
        if (!Directory.Exists(extDir))
        {
            return false;
        }

        return File.Exists(Path.Combine(extDir, $"php_{extensionId}.dll"))
               || File.Exists(Path.Combine(extDir, $"{extensionId}.dll"));
    }

    public static IEnumerable<string> DiscoverExtensions(string installPath)
    {
        var root = ResolvePackageRoot(installPath);
        var extDir = Path.Combine(root, "ext");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(extDir))
        {
            foreach (var file in Directory.EnumerateFiles(extDir, "*.dll"))
            {
                var name = ExtensionNameFromDll(Path.GetFileName(file));
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
        }

        if (File.Exists(Path.Combine(root, "php_opcache.dll")))
        {
            names.Add("opcache");
        }

        return names.OrderBy(static n => n, StringComparer.OrdinalIgnoreCase);
    }

    public static string ResolvePackageRoot(string installPath)
    {
        if (File.Exists(Path.Combine(installPath, "php.exe")))
        {
            return installPath;
        }

        var nested = Path.Combine(installPath, "bin");
        return Directory.Exists(nested) ? nested : installPath;
    }

    private static string? ExtensionNameFromDll(string filename)
    {
        if (!filename.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var baseName = filename[..^4];
        if (!baseName.StartsWith("php", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var name = baseName.StartsWith("php_", StringComparison.OrdinalIgnoreCase)
            ? baseName[4..]
            : baseName.StartsWith("php", StringComparison.OrdinalIgnoreCase) && baseName.Length > 3
                ? baseName[3..]
                : baseName;

        return string.IsNullOrWhiteSpace(name) ? null : name.ToLowerInvariant();
    }
}
