namespace Stackroot.Core.Sites.Models;

public sealed class SitesRegistry
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<Site> Sites { get; set; } = [];
}
