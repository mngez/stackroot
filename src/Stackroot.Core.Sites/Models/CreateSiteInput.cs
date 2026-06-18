namespace Stackroot.Core.Sites.Models;

public sealed class CreateSiteInput
{
    public string Name { get; init; } = string.Empty;
    public string Domain { get; init; } = string.Empty;
    public string? DomainSuffix { get; init; }
    public string Template { get; init; } = SiteTemplateIds.Static;
    public string? PhpVersionId { get; init; }
    public string? NodeVersionId { get; init; }
    public string? PathMode { get; init; }
    public string? CustomPath { get; init; }
    public bool? Enabled { get; init; }
    public bool? Featured { get; init; }

    /// <summary>Run the one-click installer after scaffolding.</summary>
    public bool Install { get; init; }

    /// <summary>Create a database for the site during install.</summary>
    public bool CreateDatabase { get; init; }

    /// <summary>Custom database name (auto-generated from domain if empty).</summary>
    public string? DatabaseName { get; init; }
}
