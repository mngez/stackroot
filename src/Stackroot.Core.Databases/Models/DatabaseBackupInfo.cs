namespace Stackroot.Core.Databases.Models;

using Stackroot.Core.Abstractions;

public sealed record DatabaseBackupInfo(
    string FileName,
    string FullPath,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    string? DatabaseName,
    SqlEngine? Engine);
