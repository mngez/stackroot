namespace Stackroot.Core.Abstractions.DataDocuments;

public sealed class PhpProfileDocument
{
    public int SchemaVersion { get; set; } = DataDocumentSchemas.PhpProfile;

    public string ExportedAt { get; set; } = string.Empty;

    public string? StackrootVersion { get; set; }

    public string TargetVersionId { get; set; } = string.Empty;

    public string? TargetVersionLabel { get; set; }

    public PhpProfileRuntime Runtime { get; set; } = new();

    public Dictionary<string, bool> Extensions { get; set; } = [];

    public Dictionary<string, string> IniOverrides { get; set; } = [];
}

public sealed class PhpProfileRuntime
{
    public string MemoryLimit { get; set; } = "512M";

    public string MaxExecutionTime { get; set; } = "120";

    public string UploadMaxFilesize { get; set; } = "64M";

    public string PostMaxSize { get; set; } = "64M";

    public bool? DisplayErrors { get; set; }

    public bool? HideWarnings { get; set; }

    public bool? HideDeprecated { get; set; }

    public bool? LogErrors { get; set; }
}
