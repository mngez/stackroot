using Stackroot.Core.Abstractions;
using Stackroot.Core.Sites.Models;

namespace Stackroot.Core.Sites.Commands;

public sealed record SiteQuickActionDefinition(
    string Id,
    string Group,
    string Label,
    SiteCommandRuntime Runtime,
    IReadOnlyList<string> Argv,
    string? Template = null,
    string? ConfirmMessage = null,
    bool Dangerous = false);

public static class SiteQuickActionPresets
{
    public static IReadOnlyDictionary<string, string> GroupLabels { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["php"] = "PHP",
            ["database"] = "Database",
            ["cache"] = "Cache",
            ["deps"] = "Dependencies",
            ["setup"] = "Setup",
            ["queue"] = "Queue"
        };

    private static readonly SiteQuickActionDefinition[] CommonPhp =
    [
        new("php-artisan-about", "php", "Artisan about", SiteCommandRuntime.Php, ["artisan", "about"], SiteTemplateIds.Laravel)
    ];

    private static readonly SiteQuickActionDefinition[] LaravelActions =
    [
        new("migrate", "database", "Run migrations", SiteCommandRuntime.Php, ["artisan", "migrate", "--force"], SiteTemplateIds.Laravel),
        new(
            "migrate-fresh",
            "database",
            "Fresh migrate + seed",
            SiteCommandRuntime.Php,
            ["artisan", "migrate:fresh", "--seed", "--force"],
            SiteTemplateIds.Laravel,
            "This drops all tables and re-runs migrations with seeders.",
            true),
        new("optimize-clear", "cache", "Clear all caches", SiteCommandRuntime.Php, ["artisan", "optimize:clear"], SiteTemplateIds.Laravel),
        new("cache-clear", "cache", "Application cache", SiteCommandRuntime.Php, ["artisan", "cache:clear"], SiteTemplateIds.Laravel),
        new("config-clear", "cache", "Config cache", SiteCommandRuntime.Php, ["artisan", "config:clear"], SiteTemplateIds.Laravel),
        new("route-clear", "cache", "Route cache", SiteCommandRuntime.Php, ["artisan", "route:clear"], SiteTemplateIds.Laravel),
        new("view-clear", "cache", "View cache", SiteCommandRuntime.Php, ["artisan", "view:clear"], SiteTemplateIds.Laravel),
        new("composer-install", "deps", "composer install", SiteCommandRuntime.Composer, ["install", "--no-interaction"], SiteTemplateIds.Laravel),
        new("composer-update", "deps", "composer update", SiteCommandRuntime.Composer, ["update", "--no-interaction"], SiteTemplateIds.Laravel),
        new("npm-install", "deps", "npm install", SiteCommandRuntime.Npm, ["install"], SiteTemplateIds.Laravel),
        new("npm-dev", "deps", "npm run dev", SiteCommandRuntime.Npm, ["run", "dev"], SiteTemplateIds.Laravel),
        new("npm-build", "deps", "npm run build", SiteCommandRuntime.Npm, ["run", "build"], SiteTemplateIds.Laravel),
        new("storage-link", "setup", "storage:link", SiteCommandRuntime.Php, ["artisan", "storage:link"], SiteTemplateIds.Laravel),
        new("queue-restart", "queue", "queue:restart", SiteCommandRuntime.Php, ["artisan", "queue:restart"], SiteTemplateIds.Laravel)
    ];

    private static readonly SiteQuickActionDefinition[] WordPressDeps =
    [
        new("composer-install", "deps", "composer install", SiteCommandRuntime.Composer, ["install", "--no-interaction"], SiteTemplateIds.Wordpress),
        new("composer-update", "deps", "composer update", SiteCommandRuntime.Composer, ["update", "--no-interaction"], SiteTemplateIds.Wordpress),
        new("npm-install", "deps", "npm install", SiteCommandRuntime.Npm, ["install"], SiteTemplateIds.Wordpress),
        new("npm-dev", "deps", "npm run dev", SiteCommandRuntime.Npm, ["run", "dev"], SiteTemplateIds.Wordpress),
        new("npm-build", "deps", "npm run build", SiteCommandRuntime.Npm, ["run", "build"], SiteTemplateIds.Wordpress)
    ];

    private static readonly Dictionary<string, SiteQuickActionDefinition> ById;

    static SiteQuickActionPresets()
    {
        ById = new Dictionary<string, SiteQuickActionDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in AllDefinitions())
        {
            ById.TryAdd(action.Id, action);
        }
    }

    public static SiteQuickActionDefinition? Get(string actionId) =>
        ById.TryGetValue(actionId, out var action) ? action : null;

    public static IReadOnlyList<SiteQuickActionDefinition> ForTemplate(string? templateId)
    {
        var normalized = string.IsNullOrWhiteSpace(templateId)
            ? SiteTemplateIds.Static
            : templateId.Trim().ToLowerInvariant();

        return normalized switch
        {
            SiteTemplateIds.Laravel => Merge(CommonPhp, LaravelActions),
            SiteTemplateIds.Wordpress => Merge(CommonPhp.Where(a => a.Template is null), WordPressDeps),
            _ => CommonPhp.Where(action => action.Template is null).ToList()
        };
    }

    private static IReadOnlyList<SiteQuickActionDefinition> Merge(
        IEnumerable<SiteQuickActionDefinition> first,
        IEnumerable<SiteQuickActionDefinition> second)
    {
        var merged = new List<SiteQuickActionDefinition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in first.Concat(second))
        {
            if (seen.Add(action.Id))
            {
                merged.Add(action);
            }
        }

        return merged;
    }

    private static IEnumerable<SiteQuickActionDefinition> AllDefinitions() =>
        CommonPhp.Concat(LaravelActions).Concat(WordPressDeps);
}
