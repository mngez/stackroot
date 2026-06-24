using System.Text;
using System.Text.RegularExpressions;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Sites.Models;
using SiteDevProxy = Stackroot.Core.Sites.Models.SiteDevProxy;

namespace Stackroot.Core.Sites.Nginx;

public static class SiteDevProxyDirectives
{
    private static readonly Regex UnsafeDirective = new(
        @"[;\r\n]|^\s*(include|rewrite|return|root|alias|fastcgi_pass|lua_|access_by)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Minimal editable defaults: proxy_pass (from <see cref="SiteDevProxy.TargetUrl"/> for legacy JSON)
    /// plus optional WebSocket headers. Everything else is user-added or appended by the vhost writer.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> BuildDefaults(SiteDevProxy proxy)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        var entries = new List<KeyValuePair<string, string>>
        {
            new("proxy_pass", proxy.TargetUrl.Trim())
        };

        if (proxy.Websocket == true)
        {
            entries.Add(new("proxy_set_header:Upgrade", "$http_upgrade"));
            entries.Add(new("proxy_set_header:Connection", "\"upgrade\""));
        }

        return entries;
    }

    public static IReadOnlyList<KeyValuePair<string, string>> BuildMerged(SiteDevProxy proxy, NginxHttpSettings? http = null)
    {
        _ = http;
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var entry in BuildDefaults(proxy))
        {
            merged[entry.Key] = entry.Value;
            order.Add(entry.Key);
        }

        if (proxy.DirectiveOverrides is not null)
        {
            foreach (var (key, value) in proxy.DirectiveOverrides)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                var normalizedKey = NormalizeOverrideKey(key);
                var normalizedValue = value?.Trim() ?? string.Empty;
                if (!order.Contains(normalizedKey, StringComparer.OrdinalIgnoreCase))
                {
                    order.Add(normalizedKey);
                }

                merged[normalizedKey] = normalizedValue;
            }
        }

        return order
            .Where(key => merged.ContainsKey(key))
            .Select(key => new KeyValuePair<string, string>(key, merged[key]))
            .ToList();
    }

    public static Dictionary<string, string>? ComputeOverrides(
        SiteDevProxy proxy,
        IReadOnlyList<KeyValuePair<string, string>> editedEntries,
        NginxHttpSettings? http = null)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        _ = http;
        var defaults = BuildDefaults(proxy)
            .ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in editedEntries)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                continue;
            }

            var key = NormalizeOverrideKey(entry.Key);
            var value = entry.Value?.Trim() ?? string.Empty;
            if (defaults.TryGetValue(key, out var defaultValue)
                && string.Equals(defaultValue, value, StringComparison.Ordinal))
            {
                continue;
            }

            overrides[key] = value;
        }

        return overrides.Count == 0 ? null : overrides;
    }

    public static string? TryGetProxyPass(SiteDevProxy proxy, NginxHttpSettings? http = null)
    {
        var merged = BuildMerged(proxy, http);
        foreach (var entry in merged)
        {
            if (string.Equals(entry.Key, "proxy_pass", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(entry.Value))
            {
                return entry.Value.Trim();
            }
        }

        return null;
    }

    public static void AppendLocationBlock(
        StringBuilder sb,
        SiteDevProxy proxy,
        NginxHttpSettings? http,
        string indent = "        ")
    {
        foreach (var entry in BuildMerged(proxy, http))
        {
            var error = ValidateEntry(entry.Key, entry.Value);
            if (error is not null)
            {
                continue;
            }

            sb.AppendLine($"{indent}{FormatDirectiveLine(entry.Key, entry.Value)};");
        }
    }

    public static string FormatDirectiveLine(string key, string value)
    {
        if (key.StartsWith("proxy_set_header:", StringComparison.OrdinalIgnoreCase))
        {
            var header = key["proxy_set_header:".Length..];
            return $"proxy_set_header {header} {value}";
        }

        return $"{key} {value}";
    }

    public static string? ValidateEntry(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return "Directive name is required.";
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return "Directive value is required.";
        }

        var line = FormatDirectiveLine(NormalizeOverrideKey(key), value.Trim());
        if (UnsafeDirective.IsMatch(line))
        {
            return "Directive is not allowed.";
        }

        return null;
    }

    public static string? ValidateProxy(SiteDevProxy proxy, NginxHttpSettings? http = null)
    {
        var proxyPass = TryGetProxyPass(proxy, http);
        if (string.IsNullOrWhiteSpace(proxyPass))
        {
            return "proxy_pass is required.";
        }

        if (!Uri.TryCreate(proxyPass, UriKind.Absolute, out var uri)
            || uri.Scheme is not "http" and not "https")
        {
            return "proxy_pass must use http:// or https://.";
        }

        foreach (var entry in BuildMerged(proxy, http))
        {
            var error = ValidateEntry(entry.Key, entry.Value);
            if (error is not null)
            {
                return error;
            }
        }

        return null;
    }

    private static string NormalizeOverrideKey(string key)
    {
        var trimmed = key.Trim();
        if (trimmed.StartsWith("proxy_set_header ", StringComparison.OrdinalIgnoreCase))
        {
            var remainder = trimmed["proxy_set_header ".Length..].Trim();
            var spaceIndex = remainder.IndexOf(' ');
            var header = spaceIndex < 0 ? remainder : remainder[..spaceIndex];
            return $"proxy_set_header:{header}";
        }

        return trimmed;
    }
}
