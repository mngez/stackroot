namespace Stackroot.Core.Supervisor;

public static class ProcessWorkingDirectory
{
    /// <summary>
    /// Matches legacy resolveProcessCwd(context.workDir, process.cwd):
    /// cwd "." uses workDir; relative cwd joins workDir; absolute cwd wins.
    /// </summary>
    public static string Resolve(string? workDir, string? cwd)
    {
        var baseDir = string.IsNullOrWhiteSpace(workDir)
            ? Environment.CurrentDirectory
            : workDir.Trim();

        var raw = string.IsNullOrWhiteSpace(cwd) ? "." : cwd.Trim();
        if (raw is "." or "./" or ".\\")
        {
            return Path.GetFullPath(baseDir);
        }

        raw = TreatUnixStyleRelativeCwd(raw);

        if (Path.IsPathRooted(raw))
        {
            return Path.GetFullPath(raw);
        }

        raw = raw.TrimStart('/', '\\');
        return Path.GetFullPath(Path.Combine(baseDir, raw));
    }

    /// <summary>
    /// On Windows, legacy Electron cwd values like "/folder/" resolve against the site workDir,
    /// not the current drive root.
    /// </summary>
    private static string TreatUnixStyleRelativeCwd(string raw)
    {
        if (!OperatingSystem.IsWindows() || raw.Length == 0)
        {
            return raw;
        }

        if (raw[0] != '/' && raw[0] != '\\')
        {
            return raw;
        }

        if (raw.Length >= 2 && raw[0] == '\\' && raw[1] == '\\')
        {
            return raw;
        }

        if (raw.Length >= 2 && char.IsLetter(raw[0]) && raw[1] == ':')
        {
            return raw;
        }

        return raw.TrimStart('/', '\\');
    }
}
