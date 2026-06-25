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

    /// <summary>Per-worker stderr log. Worker 0 keeps the legacy path for compatibility.</summary>
    public static string GetCgiWorkerStderrLogPath(string logsRoot, string versionId, int workerIndex)
        => workerIndex <= 0
            ? GetCgiStderrLogPath(logsRoot, versionId)
            : Path.Combine(logsRoot, $"php-cgi-{versionId}.w{workerIndex}.stderr.log");

    public static string ToIniPath(string path) => path.Replace('\\', '/');
}

/// <summary>
/// Shared naming for the nginx upstream that fronts a PHP version's php-cgi worker pool.
/// Both the FastCGI runtime (which writes the upstream block) and the site/vhost writers
/// (which emit <c>fastcgi_pass</c>) must agree on the name, so it lives here.
/// </summary>
public static class PhpFastCgiNaming
{
    public static string UpstreamName(string versionId)
    {
        var sanitized = new string((versionId ?? string.Empty)
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray());
        return $"php_fcgi_{sanitized}";
    }
}
