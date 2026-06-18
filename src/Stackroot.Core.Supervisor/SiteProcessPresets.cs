using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Supervisor;

public sealed record SiteProcessPresetDefinition(
    string Id,
    string Name,
    string Description,
    SiteCommandRuntime Runtime,
    IReadOnlyList<string> Argv,
    IReadOnlyList<string>? Templates = null);

public static class SiteProcessPresets
{
    private static readonly SiteProcessPresetDefinition[] Presets =
    [
        new(
            "laravel-queue-work",
            "Queue worker",
            "php artisan queue:work",
            SiteCommandRuntime.Php,
            ["artisan", "queue:work"],
            ["laravel"]),
        new(
            "laravel-schedule-work",
            "Scheduler",
            "php artisan schedule:work",
            SiteCommandRuntime.Php,
            ["artisan", "schedule:work"],
            ["laravel"]),
        new(
            "laravel-vite-dev",
            "Vite dev server",
            "npm run dev",
            SiteCommandRuntime.Npm,
            ["run", "dev"],
            ["laravel"]),
        new(
            "npm-run-dev",
            "npm run dev",
            "Front-end dev server (Node via nvm)",
            SiteCommandRuntime.Npm,
            ["run", "dev"]),
        new(
            "npm-run-watch",
            "npm run watch",
            "Watch/build script",
            SiteCommandRuntime.Npm,
            ["run", "watch"]),
        new(
            "node-script",
            "Node script",
            "node server.js — edit arguments after adding",
            SiteCommandRuntime.Node,
            ["server.js"]),
        new(
            "python-script",
            "Python script",
            "python app.py — edit arguments after adding",
            SiteCommandRuntime.Python,
            ["app.py"]),
        new(
            "shell-custom",
            "Shell command",
            "Any command line (exe, game server, etc.)",
            SiteCommandRuntime.Shell,
            ["echo Hello"])
    ];

    public static IReadOnlyList<SiteProcessPresetDefinition> ForTemplate(string? templateId)
    {
        var normalized = string.IsNullOrWhiteSpace(templateId)
            ? "static"
            : templateId.Trim().ToLowerInvariant();

        return Presets
            .Where(preset => preset.Templates is null
                || preset.Templates.Count == 0
                || preset.Templates.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    public static SiteProcessPresetDefinition? Get(string presetId) =>
        Presets.FirstOrDefault(preset => string.Equals(preset.Id, presetId, StringComparison.OrdinalIgnoreCase));

    public static GlobalProcess ToProcess(SiteProcessPresetDefinition preset, string siteId, string workDir) =>
        new()
        {
            Id = Guid.NewGuid().ToString(),
            Name = preset.Name,
            Description = preset.Description,
            Runtime = preset.Runtime,
            Argv = preset.Argv.ToList(),
            SiteId = siteId,
            WorkDir = workDir,
            Cwd = ".",
            Enabled = true,
            AutoStart = false,
            FromPreset = preset.Id
        };
}
