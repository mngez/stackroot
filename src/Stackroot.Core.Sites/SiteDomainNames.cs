using System.Text.RegularExpressions;
using Stackroot.Core.Sites.Models;

namespace Stackroot.Core.Sites;

public static partial class SiteDomainNames
{
    [GeneratedRegex(
        @"^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)(?:\.(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?))*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex LiteralDomainPattern();

    [GeneratedRegex(
        @"^\*\.(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)(?:\.(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?))*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex WildcardDomainPattern();

    public static bool IsValidAlias(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return false;
        }

        var normalized = alias.Trim().ToLowerInvariant();
        return LiteralDomainPattern().IsMatch(normalized) || WildcardDomainPattern().IsMatch(normalized);
    }

    public static List<string> ParseAliasesText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text.Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static alias => alias.ToLowerInvariant())
            .Where(static alias => alias.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string FormatAliasesText(IEnumerable<string>? aliases)
    {
        if (aliases is null)
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine, aliases.Where(static alias => !string.IsNullOrWhiteSpace(alias)));
    }

    public static List<string> NormalizeAliases(string primaryDomain, IEnumerable<string>? aliases)
    {
        var primary = primaryDomain.Trim().ToLowerInvariant();
        var output = new List<string>();

        foreach (var alias in aliases ?? [])
        {
            if (string.IsNullOrWhiteSpace(alias))
            {
                continue;
            }

            var normalized = alias.Trim().ToLowerInvariant();
            if (string.Equals(normalized, primary, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!output.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                output.Add(normalized);
            }
        }

        return output;
    }

    public static string? ValidateAliases(string primaryDomain, IEnumerable<string>? aliases)
    {
        var normalized = NormalizeAliases(primaryDomain, aliases);
        foreach (var alias in normalized)
        {
            if (!IsValidAlias(alias))
            {
                return $"Invalid domain alias: {alias}";
            }
        }

        return null;
    }

    public static IReadOnlyList<string> GetServerNames(Site site)
    {
        ArgumentNullException.ThrowIfNull(site);
        var names = new List<string> { site.Domain.Trim().ToLowerInvariant() };
        names.AddRange(NormalizeAliases(site.Domain, site.DomainAliases));
        return names;
    }

    public static string FormatNginxServerName(Site site)
    {
        return string.Join(' ', GetServerNames(site));
    }

    /// <summary>
    /// Hosts-file entries: literal names only (wildcards are not supported by the OS hosts file).
    /// </summary>
    public static IEnumerable<string> GetHostsEligibleNames(Site site)
    {
        foreach (var name in GetServerNames(site))
        {
            if (!name.StartsWith("*.", StringComparison.Ordinal))
            {
                yield return name;
            }
        }
    }

    /// <summary>
    /// Names included in the dev SSL certificate SAN list.
    /// </summary>
    public static IEnumerable<string> GetSslSanNames(Site site)
    {
        foreach (var name in GetServerNames(site))
        {
            yield return name;
        }
    }

    public static bool SharesBoundName(Site left, Site right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (string.Equals(left.Id, right.Id, StringComparison.Ordinal))
        {
            return false;
        }

        var leftNames = GetServerNames(left).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in GetServerNames(right))
        {
            if (leftNames.Contains(name))
            {
                return true;
            }
        }

        return false;
    }
}
