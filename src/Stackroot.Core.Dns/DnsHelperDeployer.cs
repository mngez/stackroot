namespace Stackroot.Core.Dns;

public static class DnsHelperDeployer
{
    public static string EnsureDeployed(string appBaseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appBaseDirectory);

        var sourceDir = StackrootDnsHelperLayout.ResolveHelperSourceDirectory(appBaseDirectory);
        var targetDir = StackrootDnsHelperLayout.ResolveStableHelperDirectory(appBaseDirectory);
        var sourceExe = Path.Combine(sourceDir, StackrootDnsHelperConstants.HelperExeName);
        var targetExe = Path.Combine(targetDir, StackrootDnsHelperConstants.HelperExeName);

        if (!File.Exists(sourceExe))
        {
            throw new FileNotFoundException(
                $"Stackroot DNS Helper not found. Expected at: {sourceExe}");
        }

        if (PathsEqual(sourceDir, targetDir))
        {
            return targetExe;
        }

        if (File.Exists(targetExe) && DnsHelperBuildIdentity.FilesMatch(sourceExe, targetExe))
        {
            return targetExe;
        }

        Directory.CreateDirectory(targetDir);
        CopyDirectory(sourceDir, targetDir);
        return targetExe;
    }

    public static string ResolveSourceHelperExePath(string appBaseDirectory) =>
        Path.Combine(
            StackrootDnsHelperLayout.ResolveHelperSourceDirectory(appBaseDirectory),
            StackrootDnsHelperConstants.HelperExeName);

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var name = Path.GetFileName(file);
            var destination = Path.Combine(targetDir, name);
            File.Copy(file, destination, overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(sourceDir))
        {
            var name = Path.GetFileName(directory);
            CopyDirectory(directory, Path.Combine(targetDir, name));
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
