namespace Stackroot.Core.Abstractions.DataDocuments;

public sealed class PhpProfilesBundleDocument
{
    public int SchemaVersion { get; set; } = DataDocumentSchemas.PhpProfilesBundle;

    public string ExportedAt { get; set; } = string.Empty;

    public string? StackrootVersion { get; set; }

    public List<PhpProfileDocument> Profiles { get; set; } = [];
}
