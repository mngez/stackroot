using System.Text.RegularExpressions;
using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Settings;

public static partial class PhpVersionSettingsSanitizer
{
    private static readonly Regex SizeToken = SizeTokenRegex();

    public static PhpVersionSettings Sanitize(PhpVersionSettings? settings)
    {
        var defaults = new PhpVersionSettings();
        if (settings is null)
        {
            return defaults;
        }

        var upload = NormalizeSize(settings.UploadMaxFilesize, defaults.UploadMaxFilesize);
        var post = NormalizeSize(settings.PostMaxSize, defaults.PostMaxSize);
        if (CompareSize(post, upload) < 0)
        {
            post = upload;
        }

        return new PhpVersionSettings
        {
            MemoryLimit = NormalizeMemoryLimit(settings.MemoryLimit, defaults.MemoryLimit),
            MaxExecutionTime = NormalizeExecutionTime(settings.MaxExecutionTime, defaults.MaxExecutionTime),
            UploadMaxFilesize = upload,
            PostMaxSize = post,
            MaxInputTime = Clamp(settings.MaxInputTime, 1, 86_400, defaults.MaxInputTime),
            MaxInputVars = Clamp(settings.MaxInputVars, 100, 100_000, defaults.MaxInputVars),
            DefaultSocketTimeout = Clamp(settings.DefaultSocketTimeout, 1, 86_400, defaults.DefaultSocketTimeout),
            RealpathCacheSize = NormalizeSize(settings.RealpathCacheSize, defaults.RealpathCacheSize),
            RealpathCacheTtl = Clamp(settings.RealpathCacheTtl, 0, 86_400, defaults.RealpathCacheTtl),
            OpcacheEnabled = settings.OpcacheEnabled,
            OpcacheEnableCli = settings.OpcacheEnableCli,
            OpcacheValidateTimestamps = settings.OpcacheValidateTimestamps,
            OpcacheRevalidateFreq = Clamp(settings.OpcacheRevalidateFreq, 0, 3600, defaults.OpcacheRevalidateFreq),
            OpcacheMemoryConsumption = Clamp(settings.OpcacheMemoryConsumption, 8, 2048, defaults.OpcacheMemoryConsumption),
            OpcacheMaxAcceleratedFiles = Clamp(settings.OpcacheMaxAcceleratedFiles, 2000, 1_000_000, defaults.OpcacheMaxAcceleratedFiles),
            ManageIniManually = settings.ManageIniManually,
            DisplayErrors = settings.DisplayErrors,
            HideWarnings = settings.HideWarnings,
            HideDeprecated = settings.HideDeprecated,
            LogErrors = settings.LogErrors,
            Extensions = settings.Extensions is null ? [] : new Dictionary<string, bool>(settings.Extensions),
            IniOverrides = settings.IniOverrides is null ? [] : new Dictionary<string, string>(settings.IniOverrides)
        };
    }

    private static string NormalizeMemoryLimit(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var trimmed = value.Trim();
        if (string.Equals(trimmed, "-1", StringComparison.Ordinal))
        {
            return "-1";
        }

        return SizeToken.IsMatch(trimmed) ? trimmed : fallback;
    }

    private static string NormalizeExecutionTime(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var trimmed = value.Trim();
        if (string.Equals(trimmed, "0", StringComparison.Ordinal))
        {
            return "0";
        }

        return int.TryParse(trimmed, out var seconds) && seconds >= 1 && seconds <= 86_400
            ? seconds.ToString()
            : fallback;
    }

    private static string NormalizeSize(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var trimmed = value.Trim();
        return SizeToken.IsMatch(trimmed) ? trimmed : fallback;
    }

    private static int CompareSize(string left, string right)
    {
        return ToBytes(left).CompareTo(ToBytes(right));
    }

    private static long ToBytes(string size)
    {
        if (string.Equals(size, "-1", StringComparison.Ordinal))
        {
            return long.MaxValue;
        }

        var trimmed = size.Trim();
        var multiplier = 1L;
        if (trimmed.EndsWith("K", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 1024;
            trimmed = trimmed[..^1];
        }
        else if (trimmed.EndsWith("M", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 1024 * 1024;
            trimmed = trimmed[..^1];
        }
        else if (trimmed.EndsWith("G", StringComparison.OrdinalIgnoreCase))
        {
            multiplier = 1024 * 1024 * 1024;
            trimmed = trimmed[..^1];
        }

        return long.TryParse(trimmed, out var value) ? value * multiplier : 0;
    }

    private static int Clamp(int value, int min, int max, int fallback)
    {
        if (value < min || value > max)
        {
            return fallback;
        }

        return value;
    }

    [GeneratedRegex(@"^\d+[kKmMgG]?$", RegexOptions.CultureInvariant)]
    private static partial Regex SizeTokenRegex();
}
