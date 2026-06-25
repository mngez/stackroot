using System.Text.Json.Nodes;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Abstractions.DataDocuments;

namespace Stackroot.Core.IO.Migrations.Migrators;

internal sealed class SettingsJsonMigrator : JsonDocumentMigrator
{
    public override string DocumentId => "settings";

    public override int TargetSchemaVersion => DataDocumentSchemas.Settings;

    public override IEnumerable<string> ResolvePaths(StackrootPaths paths, DataMigrationContext context)
    {
        yield return StackrootPathResolver.SettingsPath(paths.DataRoot);
    }

    protected override void ApplyStep(int fromVersion, int toVersion, JsonNode root)
    {
        if (root is not JsonObject obj)
        {
            return;
        }

        switch (toVersion)
        {
            case 1:
                EnsureObject(obj, "general");
                EnsureObject(obj, "php");
                EnsureObject(obj, "services");
                break;
            case 2:
                RemoveObsoleteServices(obj);
                break;
            case 3:
                EnableNginxSsl(obj);
                break;
            case 4:
                MigrateTestDnsFromSites(obj);
                break;
            case 5:
                EnsureShellMetricsEnabled(obj);
                break;
            case 6:
                EnsureShellMetricsCpuRefreshSeconds(obj);
                break;
            case 7:
                EnsureNginxHttpSettings(obj);
                break;
            case 8:
                EnsureNginxHttpExtendedSettings(obj);
                break;
            case 9:
                EnsurePhpVersionPerformanceSettings(obj);
                break;
            case 10:
                DisableRiskyPhpJitDefaults(obj);
                break;
            case 11:
                EnsureTestDnsSuffixes(obj);
                break;
            case 12:
                EnsureTestDnsLogRequests(obj);
                break;
            case 13:
                EnsureTestDnsResolveAddress(obj);
                break;
            case 14:
                EnsureTestDnsAllowDangerousSettings(obj);
                break;
            case 15:
                EnsureSslTrustMachineWideDefault(obj);
                break;
            case 16:
                EnsurePhpFpmPoolSize(obj);
                break;
        }
    }

    public override bool MigrateFile(string path, DataMigrationReport report)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var root = JsonMigrationHelper.ParseOrNull(path);
        if (root is not JsonObject obj)
        {
            return false;
        }

        var fromVersion = JsonMigrationHelper.ReadSchemaVersion(obj);
        if (fromVersion < TargetSchemaVersion)
        {
            return base.MigrateFile(path, report);
        }

        if (!RemoveObsoleteServices(obj))
        {
            return false;
        }

        JsonMigrationHelper.BackupFile(path);
        JsonMigrationHelper.WriteJson(path, obj);
        report.Record(DocumentId, path, fromVersion, TargetSchemaVersion);
        return true;
    }

    private static void MigrateTestDnsFromSites(JsonObject root)
    {
        var legacyEnabled = false;
        if (root["sites"] is JsonObject sites && sites["testDnsEnabled"] is JsonValue legacyFlag)
        {
            legacyEnabled = legacyFlag.GetValue<bool>();
            sites.Remove("testDnsEnabled");
        }

        var testDns = root["testDns"] as JsonObject ?? new JsonObject();
        if (testDns["enabled"] is null)
        {
            testDns["enabled"] = legacyEnabled;
        }

        if (testDns["autoStart"] is null)
        {
            testDns["autoStart"] = legacyEnabled || testDns["enabled"]?.GetValue<bool>() == true;
        }

        root["testDns"] = testDns;
    }

    private static void EnableNginxSsl(JsonObject root)
    {
        if (root["services"] is not JsonObject services)
        {
            return;
        }

        if (services["nginx"] is not JsonObject nginx)
        {
            return;
        }

        nginx["sslEnabled"] = true;
        if (nginx["sslPort"] is null)
        {
            nginx["sslPort"] = 443;
        }
    }

    private static void EnsureShellMetricsEnabled(JsonObject root)
    {
        if (root["general"] is not JsonObject general)
        {
            return;
        }

        if (general["shellMetricsEnabled"] is null)
        {
            general["shellMetricsEnabled"] = true;
        }
    }

    private static void EnsureNginxHttpSettings(JsonObject root)
    {
        var defaults = new NginxHttpSettings();
        var nginxHttp = root["nginxHttp"] as JsonObject ?? new JsonObject();

        SetStringIfMissing(nginxHttp, "workerProcesses", defaults.WorkerProcesses);
        SetIntIfMissing(nginxHttp, "workerConnections", defaults.WorkerConnections);
        SetIntIfMissing(nginxHttp, "keepaliveTimeout", defaults.KeepaliveTimeout);
        SetBoolIfMissing(nginxHttp, "sendfile", defaults.Sendfile);
        SetBoolIfMissing(nginxHttp, "tcpNopush", defaults.TcpNopush);
        SetStringIfMissing(nginxHttp, "clientMaxBodySize", defaults.ClientMaxBodySize);
        SetIntIfMissing(nginxHttp, "typesHashMaxSize", defaults.TypesHashMaxSize);
        SetIntIfMissing(nginxHttp, "serverNamesHashBucketSize", defaults.ServerNamesHashBucketSize);
        SetBoolIfMissing(nginxHttp, "gzipEnabled", defaults.GzipEnabled);
        SetIntIfMissing(nginxHttp, "gzipCompLevel", defaults.GzipCompLevel);
        SetIntIfMissing(nginxHttp, "gzipMinLength", defaults.GzipMinLength);

        root["nginxHttp"] = nginxHttp;
    }

    private static void EnsureNginxHttpExtendedSettings(JsonObject root)
    {
        var defaults = new NginxHttpSettings();
        var nginxHttp = root["nginxHttp"] as JsonObject ?? new JsonObject();

        SetBoolIfMissing(nginxHttp, "manageMainConfigManually", defaults.ManageMainConfigManually);
        SetBoolIfMissing(nginxHttp, "multiAccept", defaults.MultiAccept);
        SetBoolIfMissing(nginxHttp, "accessLogEnabled", defaults.AccessLogEnabled);
        SetStringIfMissing(nginxHttp, "errorLogLevel", defaults.ErrorLogLevel);
        SetIntIfMissing(nginxHttp, "fastCgiConnectTimeoutSeconds", defaults.FastCgiConnectTimeoutSeconds);
        SetIntIfMissing(nginxHttp, "fastCgiSendTimeoutSeconds", defaults.FastCgiSendTimeoutSeconds);
        SetIntIfMissing(nginxHttp, "fastCgiReadTimeoutSeconds", defaults.FastCgiReadTimeoutSeconds);
        SetIntIfMissing(nginxHttp, "proxyConnectTimeoutSeconds", defaults.ProxyConnectTimeoutSeconds);
        SetIntIfMissing(nginxHttp, "proxySendTimeoutSeconds", defaults.ProxySendTimeoutSeconds);
        SetIntIfMissing(nginxHttp, "proxyReadTimeoutSeconds", defaults.ProxyReadTimeoutSeconds);

        root["nginxHttp"] = nginxHttp;
    }

    private static void EnsurePhpVersionPerformanceSettings(JsonObject root)
    {
        if (root["php"] is not JsonObject php)
        {
            return;
        }

        var versions = php["versions"] as JsonObject;
        if (versions is null)
        {
            return;
        }

        var defaults = new PhpVersionSettings();
        foreach (var (_, versionNode) in versions)
        {
            if (versionNode is not JsonObject version)
            {
                continue;
            }

            SetIntIfMissing(version, "maxInputTime", defaults.MaxInputTime);
            SetIntIfMissing(version, "maxInputVars", defaults.MaxInputVars);
            SetIntIfMissing(version, "defaultSocketTimeout", defaults.DefaultSocketTimeout);
            SetStringIfMissing(version, "realpathCacheSize", defaults.RealpathCacheSize);
            SetIntIfMissing(version, "realpathCacheTtl", defaults.RealpathCacheTtl);
            SetBoolIfMissing(version, "opcacheEnabled", defaults.OpcacheEnabled);
            SetBoolIfMissing(version, "opcacheEnableCli", defaults.OpcacheEnableCli);
            SetBoolIfMissing(version, "opcacheValidateTimestamps", defaults.OpcacheValidateTimestamps);
            SetIntIfMissing(version, "opcacheRevalidateFreq", defaults.OpcacheRevalidateFreq);
            SetIntIfMissing(version, "opcacheMemoryConsumption", defaults.OpcacheMemoryConsumption);
            SetIntIfMissing(version, "opcacheMaxAcceleratedFiles", defaults.OpcacheMaxAcceleratedFiles);
            SetBoolIfMissing(version, "manageIniManually", defaults.ManageIniManually);
            UpgradeLegacyUploadLimits(version);
        }
    }

    private static void UpgradeLegacyUploadLimits(JsonObject version)
    {
        var upload = version["uploadMaxFilesize"]?.GetValue<string>();
        var post = version["postMaxSize"]?.GetValue<string>();
        if (string.Equals(upload, "128M", StringComparison.OrdinalIgnoreCase)
            && string.Equals(post, "128M", StringComparison.OrdinalIgnoreCase))
        {
            version["uploadMaxFilesize"] = "512M";
            version["postMaxSize"] = "512M";
        }
    }

    private static void DisableRiskyPhpJitDefaults(JsonObject root)
    {
        if (root["php"] is not JsonObject php)
        {
            return;
        }

        var versions = php["versions"] as JsonObject;
        if (versions is null)
        {
            return;
        }

        foreach (var (_, versionNode) in versions)
        {
            if (versionNode is not JsonObject version)
            {
                continue;
            }

            version.Remove("opcacheJitBufferSize");
            version.Remove("opcacheJit");
        }
    }

    private static void SetStringIfMissing(JsonObject obj, string key, string value)
    {
        if (obj[key] is null)
        {
            obj[key] = value;
        }
    }

    private static void SetIntIfMissing(JsonObject obj, string key, int value)
    {
        if (obj[key] is null)
        {
            obj[key] = value;
        }
    }

    private static void SetBoolIfMissing(JsonObject obj, string key, bool value)
    {
        if (obj[key] is null)
        {
            obj[key] = value;
        }
    }

    private static void EnsureShellMetricsCpuRefreshSeconds(JsonObject root)
    {
        if (root["general"] is not JsonObject general)
        {
            return;
        }

        if (general["shellMetricsCpuRefreshSeconds"] is null)
        {
            general["shellMetricsCpuRefreshSeconds"] = ShellMetricsDefaults.CpuRefreshSeconds;
        }
    }

    private static bool RemoveObsoleteServices(JsonObject root)
    {
        if (root["services"] is not JsonObject services)
        {
            return false;
        }

        var removeKeys = services
            .Select(pair => pair.Key)
            .Where(key => SettingsDocumentRules.ObsoleteServiceKeys.Contains(key) || !IsKnownServiceKey(key))
            .ToList();

        if (removeKeys.Count == 0)
        {
            return false;
        }

        foreach (var key in removeKeys)
        {
            services.Remove(key);
        }

        return true;
    }

    private static bool IsKnownServiceKey(string key)
    {
        foreach (var serviceId in Enum.GetValues<ServiceId>())
        {
            if (string.Equals(serviceId.ToString(), key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void EnsureTestDnsSuffixes(JsonObject root)
    {
        var testDns = root["testDns"] as JsonObject ?? new JsonObject();
        if (testDns["suffixes"] is not JsonArray suffixes || suffixes.Count == 0)
        {
            testDns["suffixes"] = new JsonArray(".test");
        }

        root["testDns"] = testDns;
    }

    private static void EnsureTestDnsLogRequests(JsonObject root)
    {
        var testDns = root["testDns"] as JsonObject ?? new JsonObject();
        SetBoolIfMissing(testDns, "logRequests", false);
        root["testDns"] = testDns;
    }

    private static void EnsureTestDnsResolveAddress(JsonObject root)
    {
        var testDns = root["testDns"] as JsonObject ?? new JsonObject();
        if (testDns["resolveAddress"] is not JsonValue)
        {
            testDns["resolveAddress"] = "127.0.0.1";
        }

        root["testDns"] = testDns;
    }

    private static void EnsureTestDnsAllowDangerousSettings(JsonObject root)
    {
        var testDns = root["testDns"] as JsonObject ?? new JsonObject();
        SetBoolIfMissing(testDns, "allowDangerousSettings", false);
        root["testDns"] = testDns;
    }

    private static void EnsurePhpFpmPoolSize(JsonObject root)
    {
        if (root["php"] is not JsonObject php)
        {
            return;
        }

        SetIntIfMissing(php, "fpmPoolSize", new PhpSettings().FpmPoolSize);
    }

    private static void EnsureSslTrustMachineWideDefault(JsonObject root)
    {
        if (root["general"] is not JsonObject general)
        {
            return;
        }

        SetBoolIfMissing(general, "trustSslCaMachineWide", false);
    }

    private static void EnsureObject(JsonObject parent, string name)
    {
        if (parent[name] is not JsonObject)
        {
            parent[name] = new JsonObject();
        }
    }
}
