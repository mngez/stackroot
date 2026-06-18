namespace Stackroot.App.ViewModels;



public sealed class ServicePackageOptionViewModel

{

    public required string Id { get; init; }

    public required string Label { get; init; }

}



public sealed class SiteLinkOptionViewModel

{

    public string? SiteId { get; init; }

    public required string Label { get; init; }

}


