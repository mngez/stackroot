namespace Stackroot.Core.Abstractions.DataDocuments;

public sealed class CustomCommandsDocument
{
    public int SchemaVersion { get; set; } = DataDocumentSchemas.SiteCustomCommands;

    public List<CustomCommandEntry> Commands { get; set; } = [];
}

public sealed class CustomCommandEntry
{
    public string Id { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string Command { get; set; } = string.Empty;

    public string? Runtime { get; set; }
}
