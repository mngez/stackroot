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

    private static void EnsureObject(JsonObject parent, string name)
    {
        if (parent[name] is not JsonObject)
        {
            parent[name] = new JsonObject();
        }
    }
}
