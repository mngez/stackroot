using System.Text.Json.Nodes;
using Stackroot.Core.IO;

namespace Stackroot.Core.IO.Migrations;

internal static class JsonMigrationHelper
{
    public static int ReadSchemaVersion(JsonNode? root)
    {
        if (root is not JsonObject obj)
        {
            return 0;
        }

        if (!obj.TryGetPropertyValue("schemaVersion", out var node))
        {
            return 0;
        }

        return node switch
        {
            JsonValue value when value.TryGetValue<int>(out var number) => number,
            JsonValue value when value.TryGetValue<long>(out var number) => (int)number,
            _ => 0
        };
    }

    public static void SetSchemaVersion(JsonObject root, int version)
    {
        root["schemaVersion"] = version;
    }

    public static void BackupFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var backupPath = $"{path}.pre-migration-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.bak";
        File.Copy(path, backupPath, overwrite: false);
    }

    public static void WriteJson(string path, JsonNode root)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = root.ToJsonString(JsonSerializerConfig.Default);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, json);

        if (File.Exists(path))
        {
            File.Replace(tempPath, path, null);
            return;
        }

        File.Move(tempPath, path);
    }

    public static JsonNode? ParseOrNull(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonNode.Parse(json);
    }
}
