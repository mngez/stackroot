using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Databases.Models;

public sealed record DatabaseRecord
{
    public string Name { get; init; } = string.Empty;
    public SqlEngine Engine { get; init; } = SqlEngine.Mysql;
    public string? SiteId { get; init; }
    public string CreatedAt { get; init; } = string.Empty;
    public string UpdatedAt { get; init; } = string.Empty;
    public string? LastBackupAt { get; init; }
}
