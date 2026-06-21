using Stackroot.Core.Sites.Models;

namespace Stackroot.App.Helpers;

public static class SiteQuickActionStatusFormatter
{
    public static string Format(string? actionId, string? actionLabel, SiteCommandResult result)
    {
        var label = string.IsNullOrWhiteSpace(actionLabel) ? "Command" : actionLabel;

        if (result.ExitCode == -1)
        {
            return $"{label} cancelled.";
        }

        if (result.ExitCode != 0)
        {
            return FormatFailure(result, label);
        }

        return ResolveStyle(actionId) switch
        {
            StatusStyle.ArtisanAbout => FormatArtisanAbout(label, result),
            StatusStyle.VerboseLog => FormatVerboseSuccess(label, result),
            StatusStyle.LastSignificantLine => FormatLastSignificantLine(result) ?? FormatVerboseSuccess(label, result),
            StatusStyle.FirstLine => FormatFirstLineSuccess(result) ?? FormatGenericSuccess(label, result),
            _ => FormatFirstLineSuccess(result) ?? FormatGenericSuccess(label, result)
        };
    }

    private enum StatusStyle
    {
        FirstLine,
        LastSignificantLine,
        ArtisanAbout,
        VerboseLog
    }

    private static StatusStyle ResolveStyle(string? actionId) =>
        actionId?.ToLowerInvariant() switch
        {
            "php-artisan-about" => StatusStyle.ArtisanAbout,
            "php-version" => StatusStyle.FirstLine,
            "migrate" or "migrate-fresh" => StatusStyle.LastSignificantLine,
            "composer-install" or "composer-update" or "npm-install" or "npm-dev" or "npm-build" =>
                StatusStyle.VerboseLog,
            _ => StatusStyle.FirstLine
        };

    private static string FormatArtisanAbout(string label, SiteCommandResult result)
    {
        var summary = TrySummarizeArtisanAbout(result.Stdout);
        var duration = FormatDuration(result.DurationMs);
        return string.IsNullOrWhiteSpace(summary)
            ? $"{label} completed in {duration}. View log for the full environment report."
            : $"{summary} · completed in {duration}. View log for the full report.";
    }

    private static string FormatVerboseSuccess(string label, SiteCommandResult result)
    {
        var duration = FormatDuration(result.DurationMs);
        return $"{label} completed in {duration}. View log for output.";
    }

    private static string FormatGenericSuccess(string label, SiteCommandResult result)
    {
        var duration = FormatDuration(result.DurationMs);
        return $"{label} completed in {duration}. View log for details.";
    }

    private static string? FormatFirstLineSuccess(SiteCommandResult result) =>
        FirstMeaningfulLine(result.Stdout);

    private static string? FormatLastSignificantLine(SiteCommandResult result)
    {
        var lines = SplitLines(result.Stdout);
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (IsNoiseLine(line))
            {
                continue;
            }

            if (line.Contains("DONE", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Nothing to migrate", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Migration table created", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Seeding", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("INFO", StringComparison.OrdinalIgnoreCase))
            {
                return line;
            }
        }

        return FirstMeaningfulLine(result.Stdout);
    }

    private static string FormatFailure(SiteCommandResult result, string label)
    {
        var errorLine = FirstMeaningfulLine(result.Stderr) ?? FirstMeaningfulLine(result.Stdout);
        return string.IsNullOrWhiteSpace(errorLine)
            ? $"{label} failed (exit {result.ExitCode}). View log for details."
            : errorLine;
    }

    private static string? TrySummarizeArtisanAbout(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        var environment = ExtractAboutValue(stdout, "Environment");
        var laravel = ExtractAboutValue(stdout, "Laravel Version");
        var php = ExtractAboutValue(stdout, "PHP Version");
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(environment))
        {
            parts.Add(environment);
        }

        if (!string.IsNullOrWhiteSpace(laravel))
        {
            parts.Add($"Laravel {laravel}");
        }

        if (!string.IsNullOrWhiteSpace(php))
        {
            parts.Add($"PHP {php}");
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static string? ExtractAboutValue(string text, string key)
    {
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            var keyIndex = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (keyIndex < 0)
            {
                continue;
            }

            var value = line[(keyIndex + key.Length)..].Trim().Trim('.').Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    private static string? FirstMeaningfulLine(string? text)
    {
        foreach (var line in SplitLines(text))
        {
            if (!IsNoiseLine(line))
            {
                return line;
            }
        }

        return null;
    }

    private static List<string> SplitLines(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static bool IsNoiseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return true;
        }

        var trimmed = line.Trim();
        if (trimmed.StartsWith('#'))
        {
            return true;
        }

        return trimmed.All(static c => c is '─' or '│' or '┌' or '┐' or '└' or '┘' or '├' or '┤' or '┬' or '┴' or '┼' or ' ' or '.');
    }

    private static string FormatDuration(long durationMs) =>
        durationMs switch
        {
            < 1000 => $"{durationMs}ms",
            < 60_000 => $"{durationMs / 1000.0:0.#}s",
            _ => $"{durationMs / 60_000.0:0.#}m"
        };
}