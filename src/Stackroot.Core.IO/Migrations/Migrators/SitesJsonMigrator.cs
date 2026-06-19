using System.Text.Json.Nodes;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Abstractions.DataDocuments;

namespace Stackroot.Core.IO.Migrations.Migrators;

internal sealed class SitesJsonMigrator : JsonDocumentMigrator
{
    public override string DocumentId => "sites";

    public override int TargetSchemaVersion => DataDocumentSchemas.Sites;

    public override IEnumerable<string> ResolvePaths(StackrootPaths paths, DataMigrationContext context)
    {
        yield return StackrootPathResolver.SitesRegistryPath(paths.DataRoot);
    }

    protected override void ApplyStep(int fromVersion, int toVersion, JsonNode root)
    {
        if (root is not JsonObject obj)
        {
            return;
        }

        if (toVersion == 1 && obj["sites"] is null)
        {
            obj["sites"] = new JsonArray();
        }
    }
}
