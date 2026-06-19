using System.Text.Json.Nodes;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Abstractions.DataDocuments;

namespace Stackroot.Core.IO.Migrations.Migrators;

internal sealed class DownloadsJsonMigrator : JsonDocumentMigrator
{
    public override string DocumentId => "downloads";

    public override int TargetSchemaVersion => DataDocumentSchemas.DownloadCache;

    public override IEnumerable<string> ResolvePaths(StackrootPaths paths, DataMigrationContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.DownloadCacheRoot))
        {
            yield return StackrootPathResolver.DownloadsRegistryPath(context.DownloadCacheRoot);
        }
    }

    protected override void ApplyStep(int fromVersion, int toVersion, JsonNode root)
    {
        if (root is not JsonObject obj)
        {
            return;
        }

        if (toVersion == 1 && obj["entries"] is not JsonArray)
        {
            obj["entries"] = new JsonArray();
        }
    }
}
