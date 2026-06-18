using System.Globalization;

namespace Stackroot.App.Scheduling;

public static class CronParser
{
    /// <summary>Returns the next DateTime after 'after' that matches the cron expression.</summary>
    public static DateTime? GetNextRun(string cron, DateTime? after = null)
    {
        var now = after ?? DateTime.Now;
        // Start from the next minute
        var candidate = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0).AddMinutes(1);

        var parts = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) return null;

        // Try up to 2 years ahead
        var limit = candidate.AddYears(2);
        while (candidate <= limit)
        {
            if (Matches(candidate.Minute, parts[0])
                && Matches(candidate.Hour, parts[1])
                && Matches(candidate.Day, parts[2])
                && Matches(candidate.Month, parts[3])
                && MatchesDayOfWeek((int)candidate.DayOfWeek, parts[4]))
            {
                return candidate;
            }
            candidate = candidate.AddMinutes(1);
        }
        return null;
    }

    private static bool Matches(int value, string field)
    {
        if (field == "*") return true;

        foreach (var part in field.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed == "*") return true;

            if (trimmed.Contains('/'))
            {
                var stepParts = trimmed.Split('/');
                var range = stepParts[0];
                if (!int.TryParse(stepParts[1], out var step)) continue;
                int start = 0, end = 59;
                if (range != "*")
                {
                    if (range.Contains('-'))
                    {
                        var rp = range.Split('-');
                        if (!int.TryParse(rp[0], out start)) continue;
                        if (!int.TryParse(rp[1], out end)) continue;
                    }
                    else if (!int.TryParse(range, out start))
                        continue;
                }
                for (var i = start; i <= end; i += step)
                    if (i == value) return true;
                return false;
            }

            if (trimmed.Contains('-'))
            {
                var rp = trimmed.Split('-');
                if (!int.TryParse(rp[0], out var lo)) continue;
                if (!int.TryParse(rp[1], out var hi)) continue;
                if (value >= lo && value <= hi) return true;
                continue;
            }

            if (int.TryParse(trimmed, out var v) && v == value) return true;
        }
        return false;
    }

    private static bool MatchesDayOfWeek(int dow, string field)
    {
        // 0 = Sunday in DateTime, cron uses 0-7 (0 and 7 both Sunday)
        if (field == "*") return true;
        foreach (var part in field.Split(','))
        {
            var trimmed = part.Trim();
            if (trimmed == "*") return true;
            if (int.TryParse(trimmed, out var v))
            {
                if (v == 7) v = 0;
                if (v == dow) return true;
            }
        }
        return false;
    }

    /// <summary>Human-readable description of a cron expression.</summary>
    public static string Describe(string cron)
    {
        var parts = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) return "Invalid";

        if (cron == "* * * * *") return "Every minute";
        if (parts[0] != "*" && parts[1] == "*" && parts[2] == "*" && parts[3] == "*" && parts[4] == "*")
        {
            if (parts[0].Contains('/'))
                return $"Every {parts[0].Split('/')[1]} minutes";
            return $"At minute {parts[0]} of every hour";
        }
        if (parts[0] != "*" && parts[1] != "*" && parts[2] == "*" && parts[3] == "*" && parts[4] == "*")
            return $"Daily at {parts[1].PadLeft(2, '0')}:{parts[0].PadLeft(2, '0')}";

        return cron;
    }
}
