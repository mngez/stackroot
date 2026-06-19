namespace Stackroot.Core.Sites.Models;

public sealed class UpdateSiteInput
{
    public string? Name { get; init; }
    public string? Domain { get; init; }
    public List<string>? DomainAliases { get; init; }
    public string? Template { get; init; }
    public string? PhpVersionId { get; init; }
    public string? NodeVersionId { get; init; }
    public string? Path { get; init; }
    public string? PathMode { get; init; }
    public string? DocumentRoot { get; init; }
    public bool? Enabled { get; init; }
    public bool? Featured { get; init; }
    public bool? ForceHttps { get; init; }
    public List<SiteDevProxy>? DevProxies { get; init; }
}
