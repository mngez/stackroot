namespace Stackroot.Core.Dns;

public static class LocalDnsNameMatcher
{
    public static bool ShouldAnswerLocally(
        string queryName,
        IReadOnlyList<string> configuredSuffixes,
        IReadOnlyList<string> localNames)
    {
        var normalized = queryName.Trim().TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (LocalDnsSuffix.ContainsCatchAll(configuredSuffixes))
        {
            return MatchesLocalName(normalized, localNames);
        }

        string? matchedSuffix = null;
        foreach (var suffix in configuredSuffixes)
        {
            if (LocalDnsSuffix.EndsWithSuffix(normalized, suffix))
            {
                matchedSuffix = suffix;
                break;
            }
        }

        if (matchedSuffix is null)
        {
            return false;
        }

        if (LocalDnsSuffix.IsSafeSuffix(matchedSuffix))
        {
            return true;
        }

        return MatchesLocalName(normalized, localNames);
    }

    public static bool MatchesLocalName(string normalizedQuery, IReadOnlyList<string> localNames)
    {
        foreach (var name in localNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var normalizedName = name.Trim().ToLowerInvariant();
            if (normalizedName.StartsWith("*.", StringComparison.Ordinal))
            {
                if (MatchesSingleLabelWildcard(normalizedQuery, normalizedName))
                {
                    return true;
                }
            }
            else if (string.Equals(normalizedQuery, normalizedName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesSingleLabelWildcard(string query, string wildcardPattern)
    {
        if (!wildcardPattern.StartsWith("*.", StringComparison.Ordinal))
        {
            return false;
        }

        var baseDomain = wildcardPattern[2..];
        if (string.Equals(query, baseDomain, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = "." + baseDomain;
        if (!query.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var prefix = query[..^suffix.Length];
        return prefix.Length > 0 && !prefix.Contains('.');
    }
}
