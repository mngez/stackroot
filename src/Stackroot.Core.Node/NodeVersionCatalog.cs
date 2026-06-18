using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Stackroot.Core.Node;

public sealed class NodeVersionCatalog
{
    private static readonly Regex SemverRegex = new(@"^\d+\.\d+\.\d+$", RegexOptions.Compiled);
    private static readonly Regex VersionSpecRegex = new(@"^\d+(\.\d+){0,2}$", RegexOptions.Compiled);

    private static readonly string[] RecommendedVersions =
    [
        "22.14.0",
        "22.12.0",
        "20.19.0",
        "20.18.1",
        "18.20.8"
    ];

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private IReadOnlyList<string>? _cachedRemoteVersions;
    private DateTimeOffset _cachedAt;

    public IReadOnlyList<string> GetRecommendedVersions() => RecommendedVersions;

    public async Task<IReadOnlyList<string>> GetRemoteVersionsAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedRemoteVersions is not null && DateTimeOffset.UtcNow - _cachedAt < TimeSpan.FromHours(6))
        {
            return _cachedRemoteVersions;
        }

        try
        {
            using var response = await HttpClient.GetAsync("https://nodejs.org/dist/index.json", cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            var versions = new List<string>();
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("version", out var versionElement))
                {
                    continue;
                }

                var raw = versionElement.GetString()?.TrimStart('v') ?? string.Empty;
                if (SemverRegex.IsMatch(raw))
                {
                    versions.Add(raw);
                }
            }

            _cachedRemoteVersions = versions
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(static v => v, StringComparer.OrdinalIgnoreCase)
                .ToList();
            _cachedAt = DateTimeOffset.UtcNow;
            return _cachedRemoteVersions;
        }
        catch
        {
            return RecommendedVersions.ToList();
        }
    }

    public async Task<IReadOnlyList<string>> BuildSuggestionsAsync(string? filter, CancellationToken cancellationToken = default)
    {
        var remote = await GetRemoteVersionsAsync(cancellationToken);
        var merged = new HashSet<string>(RecommendedVersions, StringComparer.OrdinalIgnoreCase);
        foreach (var version in remote.Take(80))
        {
            merged.Add(version);
        }

        var ordered = merged
            .OrderByDescending(static v => Array.IndexOf(RecommendedVersions, v) >= 0 ? 1 : 0)
            .ThenByDescending(static v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (string.IsNullOrWhiteSpace(filter))
        {
            return ordered.Take(30).ToList();
        }

        var query = filter.Trim().TrimStart('v');
        return ordered
            .Where(version => version.Contains(query, StringComparison.OrdinalIgnoreCase)
                || version.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .ToList();
    }

    public static void ValidateVersion(string version)
    {
        var normalized = NodePaths.NormalizeVersion(version);
        if (!VersionSpecRegex.IsMatch(normalized))
        {
            throw new ArgumentException(
                "Use a Node version like 16, 22.12, or 22.14.0 (same as nvm install).",
                nameof(version));
        }
    }

    public static bool IsFullSemver(string version)
        => SemverRegex.IsMatch(NodePaths.NormalizeVersion(version));
}
