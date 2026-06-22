using System.Text.RegularExpressions;
using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Settings;

public static partial class NginxHttpSettingsSanitizer
{
    private static readonly Regex SizeToken = SizeTokenRegex();

    public static NginxHttpSettings Sanitize(NginxHttpSettings? settings)
    {
        var defaults = new NginxHttpSettings();
        if (settings is null)
        {
            return defaults;
        }

        return new NginxHttpSettings
        {
            WorkerProcesses = NormalizeWorkerProcesses(settings.WorkerProcesses, defaults.WorkerProcesses),
            WorkerConnections = Clamp(settings.WorkerConnections, 128, 65535, defaults.WorkerConnections),
            KeepaliveTimeout = Clamp(settings.KeepaliveTimeout, 1, 3600, defaults.KeepaliveTimeout),
            Sendfile = settings.Sendfile,
            TcpNopush = settings.TcpNopush,
            ClientMaxBodySize = NormalizeSize(settings.ClientMaxBodySize, defaults.ClientMaxBodySize),
            TypesHashMaxSize = Clamp(settings.TypesHashMaxSize, 512, 8192, defaults.TypesHashMaxSize),
            ServerNamesHashBucketSize = Clamp(settings.ServerNamesHashBucketSize, 32, 512, defaults.ServerNamesHashBucketSize),
            GzipEnabled = settings.GzipEnabled,
            GzipCompLevel = Clamp(settings.GzipCompLevel, 1, 9, defaults.GzipCompLevel),
            GzipMinLength = Clamp(settings.GzipMinLength, 0, 10_485_760, defaults.GzipMinLength),
            ManageMainConfigManually = settings.ManageMainConfigManually,
            MultiAccept = settings.MultiAccept,
            AccessLogEnabled = settings.AccessLogEnabled,
            ErrorLogLevel = NormalizeErrorLogLevel(settings.ErrorLogLevel, defaults.ErrorLogLevel),
            FastCgiConnectTimeoutSeconds = Clamp(settings.FastCgiConnectTimeoutSeconds, 1, 3600, defaults.FastCgiConnectTimeoutSeconds),
            FastCgiSendTimeoutSeconds = Clamp(settings.FastCgiSendTimeoutSeconds, 1, 86_400, defaults.FastCgiSendTimeoutSeconds),
            FastCgiReadTimeoutSeconds = Clamp(settings.FastCgiReadTimeoutSeconds, 1, 86_400, defaults.FastCgiReadTimeoutSeconds),
            ProxyConnectTimeoutSeconds = Clamp(settings.ProxyConnectTimeoutSeconds, 1, 3600, defaults.ProxyConnectTimeoutSeconds),
            ProxySendTimeoutSeconds = Clamp(settings.ProxySendTimeoutSeconds, 1, 86_400, defaults.ProxySendTimeoutSeconds),
            ProxyReadTimeoutSeconds = Clamp(settings.ProxyReadTimeoutSeconds, 1, 86_400, defaults.ProxyReadTimeoutSeconds)
        };
    }

    private static string NormalizeErrorLogLevel(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "debug" or "info" or "notice" or "warn" or "error" or "crit" or "alert" or "emerg" => value.Trim().ToLowerInvariant(),
            _ => fallback
        };
    }

    private static string NormalizeWorkerProcesses(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var trimmed = value.Trim();
        if (string.Equals(trimmed, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return "auto";
        }

        return int.TryParse(trimmed, out var count) && count >= 1 && count <= 128
            ? count.ToString()
            : fallback;
    }

    private static string NormalizeSize(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var trimmed = value.Trim();
        return SizeToken.IsMatch(trimmed) ? trimmed : fallback;
    }

    private static int Clamp(int value, int min, int max, int fallback)
    {
        if (value < min || value > max)
        {
            return fallback;
        }

        return value;
    }

    [GeneratedRegex(@"^\d+[kKmMgG]?$", RegexOptions.CultureInvariant)]
    private static partial Regex SizeTokenRegex();
}
