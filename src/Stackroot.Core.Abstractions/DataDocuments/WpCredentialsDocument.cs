namespace Stackroot.Core.Abstractions.DataDocuments;

public sealed class WpCredentialsDocument
{
    public int SchemaVersion { get; set; } = DataDocumentSchemas.SiteWpCredentials;

    public string? Password { get; set; }

    public string? Engine { get; set; }

    /// <summary>plain today; future versions may use dpapi or other formats.</summary>
    public string StorageFormat { get; set; } = "plain";
}
