namespace Stackroot.Core.Catalog;

public static class PackageBinaryResolver
{
    public static string ResolvePackageRoot(string installPath, string? relativeExecutable = null)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
        {
            return installPath;
        }

        if (!string.IsNullOrWhiteSpace(relativeExecutable) &&
            File.Exists(Path.Combine(installPath, NormalizeRelative(relativeExecutable))))
        {
            return installPath;
        }

        if (Directory.Exists(Path.Combine(installPath, "bin")))
        {
            return installPath;
        }

        foreach (var subdir in Directory.EnumerateDirectories(installPath))
        {
            if (Directory.Exists(Path.Combine(subdir, "bin")))
            {
                return subdir;
            }

            if (!string.IsNullOrWhiteSpace(relativeExecutable) &&
                File.Exists(Path.Combine(subdir, NormalizeRelative(relativeExecutable))))
            {
                return subdir;
            }
        }

        return installPath;
    }

    public static string? ResolvePackageBinary(string installPath, string relativeExecutable)
    {
        if (string.IsNullOrWhiteSpace(installPath) || string.IsNullOrWhiteSpace(relativeExecutable))
        {
            return null;
        }

        var relative = NormalizeRelative(relativeExecutable);
        var fileName = Path.GetFileName(relative);
        var roots = new[]
        {
            ResolvePackageRoot(installPath, relativeExecutable),
            installPath
        }.Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(root, relative),
                         Path.Combine(root, fileName),
                         Path.Combine(root, "bin", fileName),
                         Path.Combine(root, "sbin", fileName),
                         Path.Combine(root, "cmd", fileName)
                     })
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string NormalizeRelative(string relativeExecutable) =>
        relativeExecutable.Replace('/', Path.DirectorySeparatorChar);
}
