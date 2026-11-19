using System.Text.Json;
using System.Text.Json.Serialization;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
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
        if (entry is null || !TryGetDownload(entry, phpPackageId, out var spec))
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
        var line = ResolvePackageLine(manifest, phpPackageId);
        if (line is null)
        {
            return null;
        }

        var peclName = entry.Id;
        var archive = dll.Replace(".dll", "", StringComparison.OrdinalIgnoreCase);
        var url = $"{PeclBase}/{peclName}/{release}/{archive}-{release}-{line.Value.Line}-nts-{line.Value.Toolset}-x64.zip";
        return new PeclBuildSpec(url, dll);
    }

    private static bool TryGetDownload(PhpExtensionManifestEntry entry, string phpPackageId, out JsonElement spec)
    {
        spec = default;
        if (entry.Downloads is null)
        {
            return false;
        }

        if (entry.Downloads.TryGetValue(phpPackageId, out spec))
        {
            return true;
        }

        var series = PhpSeriesFallback.SeriesOf(phpPackageId);
        if (series is null)
        {
            return false;
        }

        foreach (var pair in entry.Downloads)
        {
            if (string.Equals(PhpSeriesFallback.SeriesOf(pair.Key), series, StringComparison.OrdinalIgnoreCase))
            {
                spec = pair.Value;
                return true;
            }
        }

        return false;
    }

    private static (string Line, string Toolset)? ResolvePackageLine(PhpExtensionsManifest manifest, string phpPackageId)
    {
        var listed = manifest.PhpPackages?.FirstOrDefault(p =>
            string.Equals(p.Id, phpPackageId, StringComparison.OrdinalIgnoreCase));
        if (listed is not null && !string.IsNullOrWhiteSpace(listed.Line) && !string.IsNullOrWhiteSpace(listed.Toolset))
        {
            return (listed.Line, listed.Toolset);
        }

        var series = PhpSeriesFallback.SeriesOf(phpPackageId);
        if (series is null)
        {
            return null;
        }

        var sibling = manifest.PhpPackages?.FirstOrDefault(p =>
            string.Equals(p.Line, series, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(p.Toolset));
        if (sibling is not null)
        {
            return (series, sibling.Toolset);
        }

        var toolset = series switch
        {
            "8.4" or "8.5" or "8.6" => "vs17",
            "7.4" or "7.3" or "7.2" or "7.1" or "7.0" => "vc15",
            _ => "vs16"
        };
        return (series, toolset);
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
