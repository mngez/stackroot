using System.Diagnostics;

namespace Stackroot.Core.Windows;

public static class ProcessUptime
{
    public static bool TryGetElapsed(int pid, out TimeSpan elapsed)
    {
        elapsed = default;
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return false;
            }

            elapsed = DateTime.Now - process.StartTime;
            return elapsed >= TimeSpan.Zero;
        }
        catch
        {
            return false;
        }
    }

    public static string Format(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed.TotalDays >= 1)
        {
            var days = (int)elapsed.TotalDays;
            return $"{days}.{elapsed.Hours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
        }

        return $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
    }

    public static string? FormatFromPid(int? pid)
    {
        if (pid is not > 0)
        {
            return null;
        }

        return TryGetElapsed(pid.Value, out var elapsed) ? Format(elapsed) : null;
    }

    public static string? FormatToolTip(string? uptimeText) =>
        string.IsNullOrWhiteSpace(uptimeText) ? null : $"Uptime: {uptimeText}";
}
