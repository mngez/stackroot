using System.Text.Json.Nodes;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Abstractions.DataDocuments;

namespace Stackroot.Core.IO.Migrations.Migrators;

internal sealed class ScheduledTasksJsonMigrator : JsonDocumentMigrator
{
    public override string DocumentId => "scheduled-tasks";

    public override int TargetSchemaVersion => DataDocumentSchemas.ScheduledTasks;

    public override IEnumerable<string> ResolvePaths(StackrootPaths paths, DataMigrationContext context)
    {
        yield return StackrootPathResolver.ScheduledTasksPath(paths.DataRoot);
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
                ["tasks"] = legacyArray.DeepClone()
            };
        }
        else if (root is JsonObject obj)
        {
            if (obj["tasks"] is not JsonArray)
            {
                obj["tasks"] = new JsonArray();
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
        // v2: optional siteId per task; absent/null means app-wide (legacy tasks unchanged).
        if (toVersion == 2 && root is JsonObject obj && obj["tasks"] is not JsonArray)
        {
            obj["tasks"] = new JsonArray();
        }
    }
}
