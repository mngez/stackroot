namespace Stackroot.Core.Sites.Models;

public sealed class SitesDashboard
{
    public IReadOnlyList<Site> Featured { get; init; } = [];
    public IReadOnlyList<Site> Active { get; init; } = [];
    public IReadOnlyList<Site> Disabled { get; init; } = [];
    public int TotalCount { get; init; }
    public int ActiveCount { get; init; }
    public int DisabledCount { get; init; }
}
