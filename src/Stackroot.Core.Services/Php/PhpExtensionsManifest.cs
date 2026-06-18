using System.Text.Json;
using System.Text.Json.Serialization;
using Stackroot.Core.Abstractions;
using Stackroot.Core.IO;

namespace Stackroot.Core.Services.Php;

public sealed class PhpPackageLine
{
    public string Id { get; set; } = string.Empty;
    public string Line { get; set; } = string.Empty;
    public string Toolset { get; set; } = string.Empty;
}

public sealed class PhpExtensionManifestEntry
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Kind { get; set; } = "bundled";
    public string? Pie { get; set; }
    public string? Dll { get; set; }
    public bool? WindowsSupported { get; set; }
    public List<ServiceId>? RequiresAnyService { get; set; }
    public Dictionary<string, JsonElement>? Downloads { get; set; }
}

public sealed class PhpExtensionsManifest
{
    public int SchemaVersion { get; set; }
    public string? UpdatedAt { get; set; }
    public List<PhpPackageLine>? PhpPackages { get; set; }
    public List<string>? Builtin { get; set; }
    public List<PhpExtensionManifestEntry> Extensions { get; set; } = [];
}

public sealed record PeclBuildSpec(string ArchiveUrl, string Dll);

public sealed class PhpExtensionsManifestStore
{
    private const string PeclBase = "https://windows.php.net/downloads/pecl/releases";
    private readonly string _manifestPath;
    private PhpExtensionsManifest? _cache;

    public PhpExtensionsManifestStore(StackrootPaths paths)
    {
        _manifestPath = Path.Combine(paths.ResourcesRoot, "packages", "php-extensions.json");
    }

    public PhpExtensionsManifest Load()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        if (!File.Exists(_manifestPath))
        {
            _cache = new PhpExtensionsManifest();
            return _cache;
        }

        var json = File.ReadAllText(_manifestPath);
        _cache = JsonSerializer.Deserialize<PhpExtensionsManifest>(json, JsonSerializerConfig.Default)
                   ?? new PhpExtensionsManifest();
        return _cache;
    }

    public void Reload() => _cache = null;

    public PhpExtensionManifestEntry? GetExtension(string extensionId)
        => Load().Extensions.FirstOrDefault(e => string.Equals(e.Id, extensionId, StringComparison.OrdinalIgnoreCase));

    public PeclBuildSpec? ResolvePeclBuild(string extensionId, string phpPackageId)
    {
        var manifest = Load();
        var entry = GetExtension(extensionId);
        if (entry is null || entry.Downloads is null || !entry.Downloads.TryGetValue(phpPackageId, out var spec))
        {
            return null;
        }

        var dll = entry.Dll ?? $"php_{entry.Id}.dll";
        if (spec.ValueKind == JsonValueKind.String)
        {
            var release = spec.GetString();
            if (string.IsNullOrWhiteSpace(release))
            {
                return null;
            }

            return BuildFromRelease(entry, phpPackageId, release, dll);
        }

        if (spec.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (spec.TryGetProperty("url", out var urlElement))
        {
            var url = urlElement.GetString();
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            var customDll = spec.TryGetProperty("dll", out var dllElement) ? dllElement.GetString() : null;
            return new PeclBuildSpec(url, customDll ?? dll);
        }

        var releaseValue = spec.TryGetProperty("release", out var releaseElement) ? releaseElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(releaseValue))
        {
            return null;
        }

        var overrideDll = spec.TryGetProperty("dll", out var overrideDllElement) ? overrideDllElement.GetString() : null;
        return BuildFromRelease(entry, phpPackageId, releaseValue, overrideDll ?? dll);
    }

    public string? ResolvePieInstallSpec(string extensionId, string phpPackageId)
    {
        var entry = GetExtension(extensionId);
        if (entry is null || string.IsNullOrWhiteSpace(entry.Pie))
        {
            return null;
        }

        var build = ResolvePeclBuild(extensionId, phpPackageId);
        if (build is null)
        {
            return entry.Pie;
        }

        var release = ExtractReleaseFromArchiveUrl(build.ArchiveUrl);
        return string.IsNullOrWhiteSpace(release) ? entry.Pie : $"{entry.Pie}:{release}";
    }

    private PeclBuildSpec? BuildFromRelease(PhpExtensionManifestEntry entry, string phpPackageId, string release, string dll)
    {
        var manifest = Load();
        var pkg = manifest.PhpPackages?.FirstOrDefault(p => string.Equals(p.Id, phpPackageId, StringComparison.OrdinalIgnoreCase));
        if (pkg is null)
        {
            return null;
        }

        var peclName = entry.Id;
        var archive = dll.Replace(".dll", "", StringComparison.OrdinalIgnoreCase);
        var url = $"{PeclBase}/{peclName}/{release}/{archive}-{release}-{pkg.Line}-nts-{pkg.Toolset}-x64.zip";
        return new PeclBuildSpec(url, dll);
    }

    private static string? ExtractReleaseFromArchiveUrl(string archiveUrl)
    {
        var segments = archiveUrl.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (string.Equals(segments[i], "releases", StringComparison.OrdinalIgnoreCase)
                && i + 2 < segments.Length)
            {
                return segments[i + 2];
            }
        }

        return null;
    }
}
