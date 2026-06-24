namespace Stackroot.Core.Sites.Models;

public sealed class SiteDevProxy
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public SiteDevProxyLocationKind? LocationKind { get; set; }
    public string LocationPath { get; set; } = "/";
    public string TargetUrl { get; set; } = string.Empty;
    public bool? Websocket { get; set; }
    /// <summary>Per-proxy nginx directive overrides (keys match SiteDevProxyDirectives format).</summary>
    public Dictionary<string, string>? DirectiveOverrides { get; set; }
}

public sealed class SiteCustomCommand
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Label { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string? Runtime { get; set; } // "php", "npm", "composer", "cmd" — null means default
    public string? ForegroundHex { get; set; }
    public string? BackgroundHex { get; set; }
    public string? IconFileName { get; set; }
}

public sealed class Site
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public List<string>? DomainAliases { get; set; }
    public string Template { get; set; } = SiteTemplateIds.Static;
    public string? PhpVersionId { get; set; }
    public string? NodeVersionId { get; set; }
    public string Path { get; set; } = string.Empty;
    public string DocumentRoot { get; set; } = ".";
    public string? PathMode { get; set; }
    public bool Enabled { get; set; } = true;
    public bool? Featured { get; set; }
    public bool? ForceHttps { get; set; }
    public List<SiteDevProxy>? DevProxies { get; set; }
    public List<SiteCustomCommand>? CustomCommands { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
}
