using System.Text.Json;
using Stackroot.Core.Abstractions;
using Stackroot.Core.IO;
using Xunit;

namespace Stackroot.Core.Tests;

public sealed class PhpCatalogContractTests
{
    private static readonly string[] ExpectedPhpIds =
    [
        "php-8.5.8",
        "php-8.5.7",
        "php-8.4.23",
        "php-8.4.22",
        "php-8.3.32",
        "php-8.3.31",
        "php-8.2.32",
        "php-8.2.31",
        "php-8.1.34",
        "php-8.0.30",
        "php-7.4.33"
    ];

    [Fact]
    public void Catalog_php_entries_have_series_archive_mirrors_and_sha256()
    {
        var catalogPath = ResolveCatalogPath();
        Assert.True(File.Exists(catalogPath), $"Missing catalog at {catalogPath}");

        var catalog = JsonSerializer.Deserialize<PackageCatalog>(
            File.ReadAllText(catalogPath),
            JsonSerializerConfig.Default);
        Assert.NotNull(catalog);

        var phpPackages = catalog!.Packages
            .Where(p => p.Type == PackageType.Php)
            .ToList();

        Assert.Equal(ExpectedPhpIds.Length, phpPackages.Count);
        foreach (var expectedId in ExpectedPhpIds)
        {
            Assert.Contains(phpPackages, p => p.Id.Equals(expectedId, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var entry in phpPackages)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Series), $"Missing series for {entry.Id}");
            Assert.StartsWith(entry.Series!, entry.Version);

            Assert.NotNull(entry.Remote);
            Assert.False(string.IsNullOrWhiteSpace(entry.Remote!.Url));
            Assert.False(string.IsNullOrWhiteSpace(entry.Remote.Sha256));
            Assert.Equal(64, entry.Remote.Sha256!.Length);
            Assert.Matches("^[0-9a-fA-F]{64}$", entry.Remote.Sha256);

            var fileName = Path.GetFileName(new Uri(entry.Remote.Url).AbsolutePath);
            Assert.False(string.IsNullOrWhiteSpace(fileName));

            var mirrors = entry.Remote.Mirrors ?? [];
            Assert.NotEmpty(mirrors);

            var archiveMirror = mirrors.FirstOrDefault(m =>
                m.Contains("/downloads/releases/archives/", StringComparison.OrdinalIgnoreCase));
            Assert.False(string.IsNullOrWhiteSpace(archiveMirror), $"Missing archives mirror for {entry.Id}");
            Assert.Equal(fileName, Path.GetFileName(new Uri(archiveMirror!).AbsolutePath), ignoreCase: true);
        }

        var series84 = phpPackages
            .Where(p => string.Equals(p.Series, "8.4", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Id)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal(["php-8.4.22", "php-8.4.23"], series84);
    }

    private static string ResolveCatalogPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "resources", "packages", "catalog.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Unable to locate resources/packages/catalog.json from test base directory.");
    }
}
