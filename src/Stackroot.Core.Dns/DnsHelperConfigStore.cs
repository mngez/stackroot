using System.Text.Json;
using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Dns;

public static class DnsHelperConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static DnsHelperRuntimeConfig Build(
        StackrootPaths paths,
        TestDnsSettings testDns,
        string appDomain,
        IEnumerable<string> siteServerNames)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(testDns);

        return new DnsHelperRuntimeConfig
        {
            DataRoot = paths.DataRoot,
            LogsRoot = paths.LogsRoot,
            Enabled = testDns.Enabled,
            Listen = testDns.Enabled,
            Suffixes = LocalDnsSuffix.NormalizeList(
                testDns.Suffixes,
                ensureDefaultTest: !LocalDnsSuffix.ContainsCatchAll(testDns.Suffixes),
                allowDangerous: testDns.AllowDangerousSettings),
            LocalNames = LocalDnsCatalog.CollectNames(appDomain, siteServerNames).ToList(),
            LogRequests = testDns.LogRequests,
            ResolveAddress = LocalDnsResolveAddress.Normalize(testDns.ResolveAddress),
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    public static void Publish(DnsHelperRuntimeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        Directory.CreateDirectory(StackrootDnsHelperConstants.ConfigDirectory);
        config.UpdatedAt = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(config, JsonOptions);
        var path = StackrootDnsHelperConstants.ConfigPath;
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        if (File.Exists(path))
        {
            File.Replace(temp, path, null, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, path);
        }
    }

    /// <summary>
    /// Clears a one-shot restart token after the helper has consumed it so a later
    /// service restart does not treat a stale token as a fresh restart request.
    /// </summary>
    public static void ClearRestartToken(DnsHelperRuntimeConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!config.RestartToken.HasValue)
        {
            return;
        }

        config.RestartToken = null;
        Publish(config);
    }

    public static DnsHelperRuntimeConfig? TryRead()
    {
        var path = StackrootDnsHelperConstants.ConfigPath;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DnsHelperRuntimeConfig>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static void WriteStatus(DnsHelperRuntimeStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        Directory.CreateDirectory(StackrootDnsHelperConstants.ConfigDirectory);
        status.UpdatedAt = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(status, JsonOptions);
        File.WriteAllText(StackrootDnsHelperConstants.StatusPath, json);
    }

    public static DnsHelperRuntimeStatus? TryReadStatus()
    {
        var path = StackrootDnsHelperConstants.StatusPath;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DnsHelperRuntimeStatus>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
