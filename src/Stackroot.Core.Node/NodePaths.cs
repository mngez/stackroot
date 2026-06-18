using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Node;

public static class NodePaths
{
    public static string VersionsRoot(StackrootPaths paths)
        => Path.Combine(paths.RuntimeRoot, "nvm", "versions");

    public static string SymlinkPath(StackrootPaths paths)
        => Path.Combine(paths.RuntimeRoot, "nvm", "current");

    public static string ActiveNodeDirectory(StackrootPaths paths)
        => SymlinkPath(paths);

    public static string VersionDirectory(StackrootPaths paths, string version)
        => Path.Combine(VersionsRoot(paths), $"v{NormalizeVersion(version)}");

    public static string NormalizeVersion(string version)
    {
        var trimmed = version.Trim();
        return trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? trimmed[1..] : trimmed;
    }
}
