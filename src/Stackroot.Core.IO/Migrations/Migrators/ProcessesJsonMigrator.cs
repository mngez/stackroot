using System.Text.Json.Nodes;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Abstractions.DataDocuments;

namespace Stackroot.Core.IO.Migrations.Migrators;

internal sealed class ProcessesJsonMigrator : JsonDocumentMigrator
{
    public override string DocumentId => "processes";

    public override int TargetSchemaVersion => DataDocumentSchemas.Processes;

    public override IEnumerable<string> ResolvePaths(StackrootPaths paths, DataMigrationContext context)
    {
        yield return StackrootPathResolver.ProcessesRegistryPath(paths.DataRoot);
    }

    protected override void ApplyStep(int fromVersion, int toVersion, JsonNode root)
    {
        if (root is not JsonObject obj)
        {
            return;
        }

        if (toVersion == 1 && obj["processes"] is null)
        {
            obj["processes"] = new JsonArray();
        }
    }
}
