using System.Text.Json.Nodes;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Abstractions.DataDocuments;

namespace Stackroot.Core.IO.Migrations.Migrators;

internal sealed class WpCredentialsJsonMigrator : JsonDocumentMigrator
{
    public override string DocumentId => "wp-credentials";

    public override int TargetSchemaVersion => DataDocumentSchemas.SiteWpCredentials;

    public override IEnumerable<string> ResolvePaths(StackrootPaths paths, DataMigrationContext context)
    {
        if (!Directory.Exists(paths.SitesRoot))
        {
            yield break;
        }

        foreach (var siteDir in Directory.EnumerateDirectories(paths.SitesRoot))
        {
            var path = StackrootPathResolver.SiteWpCredentialsPath(siteDir);
            if (File.Exists(path))
            {
                yield return path;
            }
        }
    }

    protected override void ApplyStep(int fromVersion, int toVersion, JsonNode root)
    {
        if (root is not JsonObject obj || toVersion != 1)
        {
            return;
        }

        if (JsonMigrationHelper.ReadSchemaVersion(obj) >= TargetSchemaVersion)
        {
            return;
        }

        var password = ReadString(obj, "password", "Password");
        var engine = ReadString(obj, "engine", "Engine");

        obj.Clear();
        JsonMigrationHelper.SetSchemaVersion(obj, TargetSchemaVersion);
        if (password is not null)
        {
            obj["password"] = password;
        }

        if (!string.IsNullOrWhiteSpace(engine))
        {
            obj["engine"] = engine;
        }

        obj["storageFormat"] = "plain";
    }

    private static string? ReadString(JsonObject obj, string camelName, string pascalName)
    {
        if (obj.TryGetPropertyValue(camelName, out var camelNode) && camelNode is JsonValue camelValue)
        {
            return camelValue.GetValue<string>();
        }

        if (obj.TryGetPropertyValue(pascalName, out var pascalNode) && pascalNode is JsonValue pascalValue)
        {
            return pascalValue.GetValue<string>();
        }

        return null;
    }
}
