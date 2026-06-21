namespace Stackroot.Core.Sites.Models;

public static class SiteTemplateIds
{
    public const string Static = "static";
    public const string Laravel = "laravel";
    public const string Wordpress = "wordpress";
}

public sealed class SiteTemplateDefinition
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required string DocumentRoot { get; init; }
    public IReadOnlyList<SiteDevProxy> DevProxies { get; init; } = [];

    /// <summary>Whether this template has a one-click installer.</summary>
    public bool HasInstaller { get; init; }

    /// <summary>Short installer description shown next to the checkbox.</summary>
    public string? InstallerDescription { get; init; }
}

public static class SiteTemplates
{
    private static readonly IReadOnlyList<SiteTemplateDefinition> Definitions =
    [
        new SiteTemplateDefinition
        {
            Id = SiteTemplateIds.Static,
            Label = "Empty",
            DocumentRoot = "."
        },
        new SiteTemplateDefinition
        {
            Id = SiteTemplateIds.Laravel,
            Label = "Laravel",
            DocumentRoot = "public",
            HasInstaller = true,
            InstallerDescription = "Install latest Laravel via Composer"
        },
        new SiteTemplateDefinition
        {
            Id = SiteTemplateIds.Wordpress,
            Label = "WordPress",
            DocumentRoot = ".",
            HasInstaller = true,
            InstallerDescription = "Download and extract latest WordPress"
        }
    ];

    public static IReadOnlyList<SiteTemplateDefinition> List() => Definitions;

    public static SiteTemplateDefinition Resolve(string? templateId)
    {
        var template = Definitions.FirstOrDefault(x =>
            string.Equals(x.Id, templateId, StringComparison.OrdinalIgnoreCase));
        return template ?? Definitions[0];
    }
}
