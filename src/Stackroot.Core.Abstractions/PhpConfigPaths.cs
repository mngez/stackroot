namespace Stackroot.Core.Abstractions;

public static class PhpConfigPaths
{
    public static string GetDefaultIniPath(string configRoot, string versionId)
        => Path.Combine(configRoot, "php", $"{versionId}.ini");

    public static string GetAdminToolIniPath(string configRoot, string versionId)
        => Path.Combine(configRoot, "php", $"{versionId}.pma.ini");

    public static string ToNginxPhpRc(string? iniPath)
        => string.IsNullOrWhiteSpace(iniPath) ? string.Empty : iniPath.Replace('\\', '/');

    public static string? ResolveExistingDefaultIniPath(string configRoot, string versionId)
    {
        var flat = GetDefaultIniPath(configRoot, versionId);
        if (File.Exists(flat))
        {
            return flat;
        }

        var nested = Path.Combine(configRoot, "php", versionId, "php.ini");
        return File.Exists(nested) ? nested : null;
    }
}

public static class PhpLogPaths
{
    public static string GetErrorLogPath(string logsRoot, string versionId)
        => Path.Combine(logsRoot, $"{versionId}.log");

    public static string GetCgiStderrLogPath(string logsRoot, string versionId)
        => Path.Combine(logsRoot, $"php-cgi-{versionId}.stderr.log");

    public static string ToIniPath(string path) => path.Replace('\\', '/');
}
