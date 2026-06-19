using System.Text.Json.Nodes;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Abstractions.DataDocuments;
using Stackroot.Core.IO;

namespace Stackroot.Core.Settings;

internal static class SettingsJsonSanitizer
{
    public static string Repair(string json, out bool changed)
    {
        changed = false;
        if (string.IsNullOrWhiteSpace(json))
        {
            return json;
        }

        try
        {
            var node = JsonNode.Parse(json);
            if (node is not JsonObject root)
            {
                return json;
            }

            if (RemoveObsoleteServiceKeys(root))
            {
                changed = true;
            }

            if (!changed)
            {
                return json;
            }

            return node.ToJsonString(JsonSerializerConfig.Default);
        }
        catch
        {
            changed = false;
            return json;
        }
    }

    public static bool TryPersistRepairs(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            var raw = File.ReadAllText(path);
            var repaired = Repair(raw, out var changed);
            if (!changed)
            {
                return false;
            }

            var backupPath = $"{path}.pre-repair-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.bak";
            File.Copy(path, backupPath, overwrite: false);
            File.WriteAllText(path, repaired);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool RemoveObsoleteServiceKeys(JsonObject root)
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
}
