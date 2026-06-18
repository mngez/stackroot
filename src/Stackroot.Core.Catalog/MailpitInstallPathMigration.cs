using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Catalog;

public static class MailpitInstallPathMigration
{
    public static void Migrate(string runtimeRoot, InstallRegistryStore registry, PackageCatalogStore catalog)
    {
        foreach (var installed in registry.List(PackageType.Mailpit))
        {
            var entry = catalog.GetById(installed.Id);
            if (entry is null)
            {
                continue;
            }

            var expectedPath = CatalogPaths.InstallDirForPackage(runtimeRoot, entry.InstallDir);
            if (PathsEqual(installed.InstallPath, expectedPath))
            {
                continue;
            }

            if (Directory.Exists(installed.InstallPath) && !Directory.Exists(expectedPath))
            {
                var parent = Path.GetDirectoryName(expectedPath);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                Directory.Move(installed.InstallPath, expectedPath);
            }

            if (Directory.Exists(expectedPath))
            {
                registry.Register(installed with { InstallPath = expectedPath });
            }
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            Path.GetFullPath(right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            StringComparison.OrdinalIgnoreCase);
}
