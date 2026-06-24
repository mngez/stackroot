namespace Stackroot.Core.Dns;

public static class StackrootDnsHelperLayout
{
    public const string DnsHelperFolderName = "dns-helper";
    public const string AppFolderName = "app";
    public const string CurrentVersionFileName = "current.txt";

    /// <summary>
    /// Install root (e.g. %LOCALAPPDATA%\Programs\Stackroot) when running from app\{version}\ or below.
    /// </summary>
    public static string? TryResolveInstallRoot(string appBaseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appBaseDirectory);

        var current = new DirectoryInfo(Path.GetFullPath(appBaseDirectory));
        while (current is not null)
        {
            var root = current.FullName;
            if (File.Exists(Path.Combine(root, CurrentVersionFileName)))
            {
                return root;
            }

            var stableHelperExe = Path.Combine(root, DnsHelperFolderName, StackrootDnsHelperConstants.HelperExeName);
            if (Directory.Exists(Path.Combine(root, AppFolderName))
                && File.Exists(stableHelperExe))
            {
                return root;
            }

            current = current.Parent;
        }

        var versionDir = Path.GetFullPath(appBaseDirectory);
        var appDir = Directory.GetParent(versionDir);
        if (appDir is not null
            && appDir.Name.Equals(AppFolderName, StringComparison.OrdinalIgnoreCase))
        {
            return appDir.Parent?.FullName;
        }

        return null;
    }

    public static bool IsDevLayout(string appBaseDirectory) =>
        TryResolveInstallRoot(appBaseDirectory) is null;

    /// <summary>
    /// Stable path for the service binary — never under app\{version}\ or app\dns-helper.
    /// </summary>
    public static string ResolveStableHelperDirectory(string appBaseDirectory)
    {
        var installRoot = TryResolveInstallRoot(appBaseDirectory);
        if (!string.IsNullOrWhiteSpace(installRoot))
        {
            return Path.Combine(installRoot, DnsHelperFolderName);
        }

        var besideApp = Path.Combine(Path.GetFullPath(appBaseDirectory), DnsHelperFolderName);
        return besideApp;
    }

    public static string ResolveStableHelperExePath(string appBaseDirectory) =>
        Path.Combine(ResolveStableHelperDirectory(appBaseDirectory), StackrootDnsHelperConstants.HelperExeName);

    public static string ResolveHelperSourceDirectory(string appBaseDirectory)
    {
        var besideApp = Path.Combine(Path.GetFullPath(appBaseDirectory), DnsHelperFolderName);
        if (!IsLegacyAppDnsHelperDirectory(besideApp) && HelperExeExists(besideApp))
        {
            return besideApp;
        }

        var stableDir = ResolveStableHelperDirectory(appBaseDirectory);
        if (HelperExeExists(stableDir))
        {
            return stableDir;
        }

        var installRoot = TryResolveInstallRoot(appBaseDirectory);
        if (installRoot is not null)
        {
            var legacyBesideVersion = Path.Combine(appBaseDirectory, StackrootDnsHelperConstants.HelperExeName);
            if (File.Exists(legacyBesideVersion))
            {
                return Path.GetFullPath(appBaseDirectory);
            }
        }

        var legacyDevOutput = Path.Combine(Path.GetFullPath(Path.Combine(appBaseDirectory, "..")), DnsHelperFolderName);
        if (!IsLegacyAppDnsHelperDirectory(legacyDevOutput) && HelperExeExists(legacyDevOutput))
        {
            return legacyDevOutput;
        }

        return stableDir;
    }

    public static bool IsLegacyHelperBinPath(string configuredBinPath)
    {
        if (string.IsNullOrWhiteSpace(configuredBinPath))
        {
            return false;
        }

        var normalized = Path.GetFullPath(configuredBinPath.Trim().Trim('"'));
        var directory = Path.GetDirectoryName(normalized);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        if (IsLegacyAppDnsHelperDirectory(directory))
        {
            return true;
        }

        var parent = Directory.GetParent(directory);
        var grandParentName = parent?.Parent?.Name;
        return grandParentName?.Equals(AppFolderName, StringComparison.OrdinalIgnoreCase) == true
               && parent?.Name is not null
               && Version.TryParse(parent.Name, out _);
    }

    private static bool HelperExeExists(string directory) =>
        File.Exists(Path.Combine(directory, StackrootDnsHelperConstants.HelperExeName));

    private static bool IsLegacyAppDnsHelperDirectory(string directory)
    {
        var parentName = Directory.GetParent(directory)?.Name;
        return parentName?.Equals(AppFolderName, StringComparison.OrdinalIgnoreCase) == true
               && Path.GetFileName(directory).Equals(DnsHelperFolderName, StringComparison.OrdinalIgnoreCase);
    }
}
