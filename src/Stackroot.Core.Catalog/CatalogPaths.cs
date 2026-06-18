namespace Stackroot.Core.Catalog;

public static class CatalogPaths
{
    public static string CatalogPath(string resourcesRoot)
        => Path.Combine(resourcesRoot, "packages", "catalog.json");

    public static string RegistryPath(string dataRoot)
        => Path.Combine(dataRoot, "installed.json");

    public static string BundledArchivePath(string resourcesRoot, string archive)
        => Path.Combine(resourcesRoot, "packages", archive.Replace('\\', Path.DirectorySeparatorChar));

    public static string InstallDirForPackage(string runtimeRoot, string installDir)
        => Path.Combine(runtimeRoot, installDir);
}
