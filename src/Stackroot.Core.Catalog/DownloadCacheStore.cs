using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Stackroot.Core.Abstractions;
using Stackroot.Core.IO;

namespace Stackroot.Core.Catalog;

public sealed class DownloadCacheStore
{
    private const int SchemaVersion = 1;
    private const string RegistryFileName = "downloads.json";
    private static readonly string[] ArchiveExtensions = [".zip", ".7z", ".tgz", ".gz", ".phar"];

    private readonly Func<string> _resolveRoot;

    public DownloadCacheStore(Func<string> resolveRoot)
    {
        _resolveRoot = resolveRoot;
    }

    public string CacheRoot
    {
        get
        {
            var root = Path.GetFullPath(_resolveRoot());
            Directory.CreateDirectory(root);
            return root;
        }
    }

    public static string ResolveCacheRoot(string dataRoot, string? settingsPath = null)
    {
        if (!string.IsNullOrWhiteSpace(settingsPath))
        {
            return Path.GetFullPath(settingsPath);
        }

        var fromEnv = Environment.GetEnvironmentVariable("STACKROOT_DOWNLOAD_CACHE");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        var testEnv = Environment.GetEnvironmentVariable("STACKROOT_TEST_CACHE");
        if (!string.IsNullOrWhiteSpace(testEnv))
        {
            return Path.GetFullPath(testEnv);
        }

        return Path.Combine(dataRoot, "downloads");
    }

    public static string ResolveCacheFileName(string url, string? packageId, string extension)
    {
        if (Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
        {
            var segment = Path.GetFileName(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(segment) && segment.Contains('.', StringComparison.Ordinal))
            {
                return SanitizeFileName(segment);
            }
        }

        if (!string.IsNullOrWhiteSpace(packageId))
        {
            return SanitizeFileName($"{packageId}{extension}");
        }

        return HashForUrl(url);
    }

    public string RegistryPath => Path.Combine(CacheRoot, RegistryFileName);

    public bool TryResolveCachedArchive(string packageId, string url, string extension, out string path)
    {
        var namedPath = Path.Combine(CacheRoot, ResolveCacheFileName(url, packageId, extension));
        if (File.Exists(namedPath))
        {
            path = namedPath;
            return true;
        }

        var legacyHashPath = Path.Combine(CacheRoot, HashForUrl(url));
        if (File.Exists(legacyHashPath))
        {
            path = legacyHashPath;
            return true;
        }

        var packagePath = Path.Combine(CacheRoot, SanitizeFileName($"{packageId}{extension}"));
        if (!string.Equals(packagePath, namedPath, StringComparison.OrdinalIgnoreCase) && File.Exists(packagePath))
        {
            path = packagePath;
            return true;
        }

        path = namedPath;
        return false;
    }

    public string GetDestinationPath(string packageId, string url, string extension)
        => Path.Combine(CacheRoot, ResolveCacheFileName(url, packageId, extension));

    public bool IsManagedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        var root = Path.GetFullPath(CacheRoot);
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    public void RegisterDownload(string packageId, string url, string filePath, string? sha256 = null)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        var registry = LoadRegistry();
        var fileName = Path.GetFileName(filePath);
        registry.Entries.RemoveAll(entry =>
            string.Equals(entry.FileName, fileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.PackageId, packageId, StringComparison.OrdinalIgnoreCase));

        var info = new FileInfo(filePath);
        registry.Entries.Add(new DownloadCacheEntry
        {
            PackageId = packageId,
            FileName = fileName,
            Url = url,
            SizeBytes = info.Length,
            Sha256 = sha256,
            DownloadedAt = info.LastWriteTimeUtc.ToString("O")
        });

        SaveRegistry(registry);
    }

    public IReadOnlyList<DownloadCacheEntry> List()
    {
        var registry = LoadRegistry();
        var byFileName = registry.Entries.ToDictionary(
            entry => entry.FileName,
            entry => entry,
            StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(CacheRoot))
        {
            return registry.Entries;
        }

        foreach (var file in Directory.EnumerateFiles(CacheRoot))
        {
            var fileName = Path.GetFileName(file);
            if (string.Equals(fileName, RegistryFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!ArchiveExtensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase)
                && !fileName.Contains('.', StringComparison.Ordinal))
            {
                continue;
            }

            if (byFileName.ContainsKey(fileName))
            {
                continue;
            }

            var info = new FileInfo(file);
            var orphan = new DownloadCacheEntry
            {
                PackageId = Path.GetFileNameWithoutExtension(fileName),
                FileName = fileName,
                SizeBytes = info.Length,
                DownloadedAt = info.LastWriteTimeUtc.ToString("O")
            };
            registry.Entries.Add(orphan);
            byFileName[fileName] = orphan;
        }

        return registry.Entries
            .OrderByDescending(entry => entry.DownloadedAt, StringComparer.Ordinal)
            .ToList();
    }

    public bool Remove(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var safeName = Path.GetFileName(fileName);
        var path = Path.Combine(CacheRoot, safeName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        var registry = LoadRegistry();
        var removed = registry.Entries.RemoveAll(entry =>
            string.Equals(entry.FileName, safeName, StringComparison.OrdinalIgnoreCase)) > 0;
        if (removed)
        {
            SaveRegistry(registry);
        }

        return File.Exists(path) == false;
    }

    private DownloadCacheRegistry LoadRegistry()
    {
        if (!File.Exists(RegistryPath))
        {
            return new DownloadCacheRegistry { SchemaVersion = SchemaVersion, Entries = [] };
        }

        try
        {
            var json = File.ReadAllText(RegistryPath);
            return JsonSerializer.Deserialize<DownloadCacheRegistry>(json, JsonSerializerConfig.Default)
                ?? new DownloadCacheRegistry { SchemaVersion = SchemaVersion, Entries = [] };
        }
        catch
        {
            return new DownloadCacheRegistry { SchemaVersion = SchemaVersion, Entries = [] };
        }
    }

    private void SaveRegistry(DownloadCacheRegistry registry)
    {
        Directory.CreateDirectory(CacheRoot);
        var json = JsonSerializer.Serialize(
            registry with { SchemaVersion = SchemaVersion },
            JsonSerializerConfig.Default);
        File.WriteAllText(RegistryPath, json);
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(fileName.Length);
        foreach (var ch in fileName)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        return builder.ToString();
    }

    private static string HashForUrl(string url)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
