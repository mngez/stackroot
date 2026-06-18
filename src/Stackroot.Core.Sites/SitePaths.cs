using Stackroot.Core.Sites.Models;

namespace Stackroot.Core.Sites;

public sealed record SitePathPreview(string Domain, string Path, string WwwPath, string PathMode);

public static class SitePaths
{
    public static string DefaultWwwPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "www");

    public static string EffectiveWwwPath(string? configuredWwwPath) =>
        string.IsNullOrWhiteSpace(configuredWwwPath) ? DefaultWwwPath() : configuredWwwPath.Trim();

    public static string BuildDomain(string domain, string? domainSuffix)
    {
        var baseDomain = domain.Trim();
        if (!string.IsNullOrWhiteSpace(domainSuffix) &&
            !baseDomain.Contains('.', StringComparison.Ordinal))
        {
            return $"{baseDomain}.{domainSuffix.Trim()}".ToLowerInvariant();
        }

        return baseDomain.ToLowerInvariant();
    }

    public static string ResolveSitePath(CreateSiteInput input, string? configuredWwwPath)
    {
        if (IsCustomPathMode(input))
        {
            if (string.IsNullOrWhiteSpace(input.CustomPath))
            {
                throw new InvalidOperationException("Choose a folder for the site.");
            }

            return input.CustomPath.Trim();
        }

        var domain = BuildDomain(input.Domain, input.DomainSuffix);
        return Path.Combine(EffectiveWwwPath(configuredWwwPath), domain);
    }

    public static SitePathPreview Preview(CreateSiteInput input, string? configuredWwwPath)
    {
        var pathMode = IsCustomPathMode(input) ? "custom" : "default";
        var domainInput = string.IsNullOrWhiteSpace(input.Domain) ? "site" : input.Domain.Trim();
        var domain = BuildDomain(domainInput, input.DomainSuffix);
        if (string.IsNullOrWhiteSpace(domain))
        {
            domain = "site.test";
        }

        var wwwPath = EffectiveWwwPath(configuredWwwPath);
        var path = pathMode == "custom"
            ? input.CustomPath?.Trim() ?? string.Empty
            : Path.Combine(wwwPath, domain);

        return new SitePathPreview(domain, path, wwwPath, pathMode);
    }

    public static bool IsCustomPathMode(CreateSiteInput input) =>
        string.Equals(input.PathMode, "custom", StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrWhiteSpace(input.CustomPath);
}
