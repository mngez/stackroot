using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Xunit;

namespace Stackroot.Core.Tests;

public sealed class PhpSeriesFallbackTests
{
    [Fact]
    public void PreferInstalled_keeps_the_exact_build_when_it_is_installed()
    {
        var registry = CreateRegistry(("php-8.0.2", "8.0.2"), ("php-8.0.3", "8.0.3"));

        Assert.Equal("php-8.0.2", PhpSeriesFallback.PreferInstalled(registry, "php-8.0.2"));
    }

    [Fact]
    public void PreferInstalled_moves_to_the_newest_build_in_the_same_series()
    {
        var registry = CreateRegistry(("php-8.0.1", "8.0.1"), ("php-8.0.3", "8.0.3"));

        Assert.Equal("php-8.0.3", PhpSeriesFallback.PreferInstalled(registry, "php-8.0.2"));
    }

    [Fact]
    public void PreferInstalled_does_not_cross_series()
    {
        var registry = CreateRegistry(("php-8.1.34", "8.1.34"));

        Assert.Equal("php-8.0.2", PhpSeriesFallback.PreferInstalled(registry, "php-8.0.2"));
    }

    [Fact]
    public void List_includes_an_installed_build_that_left_the_catalog()
    {
        var root = CreateTempDirectory();
        try
        {
            var registry = new InstallRegistryStore(root);
            var installPath = Path.Combine(root, "runtime", "php", "8.0.2");
            registry.Register(new InstalledPackage
            {
                Id = "php-8.0.2",
                Type = PackageType.Php,
                Version = "8.0.2",
                InstallPath = installPath,
                InstalledAt = "2024-01-01T00:00:00Z"
            });

            var catalog = new PackageCatalogStore(root);
            var listed = InstalledPhpPackages.List(catalog, registry);
            var entry = Assert.Single(listed);

            Assert.Equal("php-8.0.2", entry.Id);
            Assert.Equal("PHP 8.0.2", entry.Label);
            Assert.Equal(installPath, entry.InstallDir);

            var resolved = InstalledPhpPackages.ResolveInstalled(catalog, registry, "php-8.0.2");
            Assert.NotNull(resolved);
            Assert.Equal(installPath, resolved!.InstallDir);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static InstallRegistryStore CreateRegistry(params (string Id, string Version)[] packages)
    {
        var root = CreateTempDirectory();
        var registry = new InstallRegistryStore(root);
        foreach (var package in packages)
        {
            registry.Register(new InstalledPackage
            {
                Id = package.Id,
                Type = PackageType.Php,
                Version = package.Version,
                InstallPath = Path.Combine(root, package.Id),
                InstalledAt = "2024-01-01T00:00:00Z"
            });
        }

        return registry;
    }

    private static string CreateTempDirectory()
        => Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "stackroot-tests", Guid.NewGuid().ToString("N"))).FullName;

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best effort
        }
    }
}
