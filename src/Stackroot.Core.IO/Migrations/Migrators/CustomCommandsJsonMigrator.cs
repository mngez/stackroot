using System.Text.Json.Nodes;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Abstractions.DataDocuments;

namespace Stackroot.Core.IO.Migrations.Migrators;

internal sealed class CustomCommandsJsonMigrator : JsonDocumentMigrator
{
    public override string DocumentId => "custom-commands";

    public override int TargetSchemaVersion => DataDocumentSchemas.SiteCustomCommands;

    public override IEnumerable<string> ResolvePaths(StackrootPaths paths, DataMigrationContext context)
    {
        if (!Directory.Exists(paths.SitesRoot))
        {
            yield break;
        }

        foreach (var siteDir in Directory.EnumerateDirectories(paths.SitesRoot))
        {
            var path = StackrootPathResolver.SiteCustomCommandsPath(siteDir);
            if (File.Exists(path))
            {
                yield return path;
            }
        }
    }

    public override bool MigrateFile(string path, DataMigrationReport report)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var root = JsonMigrationHelper.ParseOrNull(path);
        if (root is null)
        {
            return false;
        }

        var fromVersion = root is JsonArray ? 0 : JsonMigrationHelper.ReadSchemaVersion(root);
        if (fromVersion >= TargetSchemaVersion)
        {
            return false;
        }

        JsonMigrationHelper.BackupFile(path);

        JsonNode output;
        if (root is JsonArray legacyArray)
        {
            output = new JsonObject
            {
                ["schemaVersion"] = TargetSchemaVersion,
                ["commands"] = legacyArray.DeepClone()
            };
        }
        else if (root is JsonObject obj)
        {
            if (obj["commands"] is not JsonArray)
            {
                obj["commands"] = new JsonArray();
            }

            JsonMigrationHelper.SetSchemaVersion(obj, TargetSchemaVersion);
            output = obj;
        }
        else
        {
            return false;
        }

        JsonMigrationHelper.WriteJson(path, output);
        report.Record(DocumentId, path, fromVersion, TargetSchemaVersion);
        return true;
    }

    protected override void ApplyStep(int fromVersion, int toVersion, JsonNode root)
    {
    }
}
