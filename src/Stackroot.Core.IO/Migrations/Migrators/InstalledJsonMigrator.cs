using System.Text.Json.Nodes;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Abstractions.DataDocuments;

namespace Stackroot.Core.IO.Migrations.Migrators;

internal sealed class InstalledJsonMigrator : JsonDocumentMigrator
{
    public override string DocumentId => "installed";

    public override int TargetSchemaVersion => DataDocumentSchemas.Installed;

    public override IEnumerable<string> ResolvePaths(StackrootPaths paths, DataMigrationContext context)
    {
        yield return StackrootPathResolver.RegistryPath(paths.DataRoot);
    }

    protected override void ApplyStep(int fromVersion, int toVersion, JsonNode root)
    {
        if (root is not JsonObject obj)
        {
            return;
        }

        if (toVersion == 1 && obj["packages"] is null)
        {
            obj["packages"] = new JsonArray();
        }
    }
}
