using System.Text.Json.Nodes;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Abstractions.DataDocuments;

namespace Stackroot.Core.IO.Migrations.Migrators;

internal sealed class DatabasesJsonMigrator : JsonDocumentMigrator
{
    public override string DocumentId => "databases";

    public override int TargetSchemaVersion => DataDocumentSchemas.Databases;

    public override IEnumerable<string> ResolvePaths(StackrootPaths paths, DataMigrationContext context)
    {
        yield return StackrootPathResolver.DatabasesRegistryPath(paths.DataRoot);
    }

    protected override void ApplyStep(int fromVersion, int toVersion, JsonNode root)
    {
        if (root is not JsonObject obj)
        {
            return;
        }

        if (toVersion == 1 && obj["databases"] is null)
        {
            obj["databases"] = new JsonArray();
        }
    }
}
