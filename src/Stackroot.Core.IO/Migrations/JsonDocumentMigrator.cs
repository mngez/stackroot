using System.Text.Json.Nodes;
using Stackroot.Core.Abstractions;

namespace Stackroot.Core.IO.Migrations;

internal abstract class JsonDocumentMigrator
{
    public abstract string DocumentId { get; }

    public abstract int TargetSchemaVersion { get; }

    public abstract IEnumerable<string> ResolvePaths(StackrootPaths paths, DataMigrationContext context);

    public virtual bool MigrateFile(string path, DataMigrationReport report)
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

        var fromVersion = DetectSchemaVersion(root);
        if (fromVersion >= TargetSchemaVersion)
        {
            return false;
        }

        JsonMigrationHelper.BackupFile(path);

        var version = fromVersion;
        while (version < TargetSchemaVersion)
        {
            var next = version + 1;
            ApplyStep(version, next, root);
            version = next;
        }

        FinalizeDocument(root, TargetSchemaVersion);
        JsonMigrationHelper.WriteJson(path, root);
        report.Record(DocumentId, path, fromVersion, TargetSchemaVersion);
        return true;
    }

    protected virtual int DetectSchemaVersion(JsonNode root) => JsonMigrationHelper.ReadSchemaVersion(root);

    protected abstract void ApplyStep(int fromVersion, int toVersion, JsonNode root);

    protected virtual void FinalizeDocument(JsonNode root, int targetVersion)
    {
        if (root is JsonObject obj)
        {
            JsonMigrationHelper.SetSchemaVersion(obj, targetVersion);
        }
    }
}

public sealed class DataMigrationContext
{
    public string? DownloadCacheRoot { get; set; }
}
