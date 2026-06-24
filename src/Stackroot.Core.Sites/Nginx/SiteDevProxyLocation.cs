using System.Text.RegularExpressions;
using Stackroot.Core.Sites.Models;

namespace Stackroot.Core.Sites.Nginx;

public static class SiteDevProxyLocation
{
    public static string Format(SiteDevProxyLocationKind kind, string pattern)
    {
        var trimmed = pattern.Trim();
        return kind switch
        {
            SiteDevProxyLocationKind.Exact => $"= {trimmed}",
            SiteDevProxyLocationKind.Regex => FormatRegexModifier("~", trimmed),
            SiteDevProxyLocationKind.RegexIgnoreCase => FormatRegexModifier("~*", trimmed),
            _ => trimmed.StartsWith('/') ? trimmed : $"/{trimmed}"
        };
    }

    public static string Format(SiteDevProxy proxy)
        => Format(ResolveKind(proxy), proxy.LocationPath);

    public static (SiteDevProxyLocationKind Kind, string Pattern) Normalize(
        SiteDevProxyLocationKind? kind,
        string? locationPath)
    {
        var raw = string.IsNullOrWhiteSpace(locationPath) ? "/" : locationPath.Trim();
        if (kind is SiteDevProxyLocationKind explicitKind
            && explicitKind != SiteDevProxyLocationKind.Prefix)
        {
            return (explicitKind, ParsePatternInput(explicitKind, raw));
        }

        if (raw.StartsWith("~* ", StringComparison.Ordinal))
        {
            return (SiteDevProxyLocationKind.RegexIgnoreCase, ParsePatternInput(SiteDevProxyLocationKind.RegexIgnoreCase, raw));
        }

        if (raw.StartsWith('~') && (raw.Length == 1 || raw[1] == ' '))
        {
            return (SiteDevProxyLocationKind.Regex, ParsePatternInput(SiteDevProxyLocationKind.Regex, raw));
        }

        if (raw.StartsWith("= ", StringComparison.Ordinal))
        {
            return (SiteDevProxyLocationKind.Exact, ParsePatternInput(SiteDevProxyLocationKind.Exact, raw));
        }

        var cleaned = ParsePatternInput(SiteDevProxyLocationKind.Prefix, raw);
        if (LooksLikeRegex(cleaned))
        {
            return (SiteDevProxyLocationKind.Regex, cleaned);
        }

        return (SiteDevProxyLocationKind.Prefix, cleaned);
    }

    public static string ParsePatternInput(SiteDevProxyLocationKind kind, string raw)
    {
        var text = raw.Trim();
        if (text.Contains('\r') || text.Contains('\n'))
        {
            text = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(static line => line.Trim())
                .FirstOrDefault(static line => line.Length > 0) ?? string.Empty;
        }

        if (text.StartsWith("location", StringComparison.OrdinalIgnoreCase))
        {
            text = text["location".Length..].TrimStart();
        }

        if (text.EndsWith('{'))
        {
            text = text[..^1].TrimEnd();
        }

        return StripModifierPrefix(text);
    }

    public static string? Validate(SiteDevProxyLocationKind kind, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return "Location pattern is required.";
        }

        var trimmed = pattern.Trim();
        return kind switch
        {
            SiteDevProxyLocationKind.Prefix when LooksLikeRegex(trimmed)
                => "This looks like a regex — set Match type to Regex.",
            SiteDevProxyLocationKind.Prefix when !trimmed.StartsWith('/')
                => "Prefix paths must start with /.",
            SiteDevProxyLocationKind.Exact when !trimmed.StartsWith('/')
                => "Exact paths must start with /.",
            SiteDevProxyLocationKind.Regex or SiteDevProxyLocationKind.RegexIgnoreCase
                => ValidateRegexPattern(trimmed, ignoreCase: kind == SiteDevProxyLocationKind.RegexIgnoreCase),
            _ => null
        };
    }

    public static bool LooksLikeRegex(string pattern)
        => pattern.StartsWith('^') || pattern.Contains('|');

    private static SiteDevProxyLocationKind ResolveKind(SiteDevProxy proxy)
        => Normalize(proxy.LocationKind, proxy.LocationPath).Kind;

    private static string StripModifierPrefix(string raw)
    {
        if (raw.StartsWith("~* ", StringComparison.Ordinal))
        {
            return raw[3..].Trim();
        }

        if (raw.StartsWith('~') && (raw.Length == 1 || raw[1] == ' '))
        {
            return raw.TrimStart('~').TrimStart();
        }

        if (raw.StartsWith("= ", StringComparison.Ordinal))
        {
            return raw[2..].Trim();
        }

        return UnquotePattern(raw);
    }

    private static string FormatRegexModifier(string modifier, string pattern)
    {
        if (pattern.Contains('{') || pattern.Contains('}'))
        {
            return $"{modifier} \"{EscapeNginxQuotedString(pattern)}\"";
        }

        return $"{modifier} {pattern}";
    }

    private static string EscapeNginxQuotedString(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string UnquotePattern(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            return UnescapeNginxQuotedString(trimmed[1..^1]);
        }

        return trimmed;
    }

    private static string UnescapeNginxQuotedString(string value)
        => value.Replace("\\\"", "\"").Replace("\\\\", "\\");

    private static string? ValidateRegexPattern(string pattern, bool ignoreCase)
    {
        try
        {
            _ = new Regex(
                pattern,
                ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None,
                matchTimeout: TimeSpan.FromMilliseconds(100));
        }
        catch (Exception ex)
        {
            return $"Invalid regex pattern: {ex.Message}";
        }

        if (!pattern.StartsWith('^'))
        {
            return "Regex patterns should usually start with ^.";
        }

        return null;
    }
}
