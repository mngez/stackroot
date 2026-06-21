namespace Stackroot.Core.Abstractions;

public static class ShellMetricsDefaults
{
    public const int CpuRefreshSeconds = 12;
    public const int MinCpuRefreshSeconds = 6;
    public const int MaxCpuRefreshSeconds = 120;

    public static int ClampCpuRefreshSeconds(int seconds)
        => Math.Clamp(seconds, MinCpuRefreshSeconds, MaxCpuRefreshSeconds);
}
