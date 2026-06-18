namespace Stackroot.Core.Databases.Models;

public sealed record DatabasesRegistry
{
    public int SchemaVersion { get; init; } = 1;
    public List<DatabaseRecord> Databases { get; init; } = [];
}
