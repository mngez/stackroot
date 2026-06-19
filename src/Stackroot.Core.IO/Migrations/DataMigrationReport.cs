namespace Stackroot.Core.IO.Migrations;

public sealed class DataMigrationReport
{
    public List<DataMigrationChange> Changes { get; } = [];

    public bool HasChanges => Changes.Count > 0;

    public void Record(string documentId, string path, int fromVersion, int toVersion)
    {
        Changes.Add(new DataMigrationChange(documentId, path, fromVersion, toVersion));
    }
}

public sealed record DataMigrationChange(string DocumentId, string Path, int FromVersion, int ToVersion);
