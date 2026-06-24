using System.Diagnostics;
using System.Text;
using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Dns;

/// <summary>
/// Routes configured dev suffixes to Stackroot's local resolver via Windows NRPT.
/// </summary>
public sealed class WindowsNrptManager
{
    public const string LegacyRuleDisplayName = "Stackroot .test DNS";
    public const string RuleDisplayNamePrefix = "Stackroot DNS";
    public const string NameServer = "127.0.0.1";

    private static readonly object DisplayNameCacheSync = new();
    private static readonly TimeSpan DisplayNameCacheTtl = TimeSpan.FromSeconds(15);
    private static HashSet<string> _cachedDisplayNames = [];
    private static DateTimeOffset _displayNamesExpireAt;

    public bool IsRulePresent() => AreAllRulesPresent([LocalDnsSuffix.TryNormalize(".test")!]);

    public bool AreAllRulesPresent(IReadOnlyList<string> suffixes)
    {
        var normalized = NormalizeSuffixes(suffixes, ensureDefaultTest: false);
        if (normalized.Count == 0)
        {
            return QueryPresentDisplayNames().Count == 0;
        }

        var present = QueryPresentDisplayNames();
        foreach (var suffix in normalized)
        {
            if (!IsSuffixCovered(suffix, present))
            {
                return false;
            }
        }

        return true;
    }

    public bool TryEnable(out string? error) => TrySyncRules([".test"], out error);

    public bool TrySyncRules(IReadOnlyList<string> suffixes, out string? error, bool allowElevation = true)
    {
        var normalized = NormalizeSuffixes(suffixes);
        if (IsInSync(normalized))
        {
            error = null;
            return true;
        }

        var script = BuildSyncScript(normalized);
        if (RunPowerShell(script, elevate: false, out error) == 0)
        {
            error = null;
            InvalidateDisplayNameCache();
            return true;
        }

        if (allowElevation && RunPowerShell(script, elevate: true, out error) == 0)
        {
            error = null;
            InvalidateDisplayNameCache();
            return true;
        }

        error ??= "Could not register dev DNS routing rules (NRPT).";
        return false;
    }

    public bool HasAnyStackrootRules() => QueryPresentDisplayNames().Count > 0;

    public bool TryDisable(out string? error, bool allowElevation = true)
    {
        if (!HasAnyStackrootRules())
        {
            error = null;
            return true;
        }

        var script =
            "Get-DnsClientNrptRule -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -eq 'Stackroot .test DNS' -or $_.DisplayName -like 'Stackroot DNS*' } | Remove-DnsClientNrptRule -Force -ErrorAction SilentlyContinue";
        if (RunPowerShell(script, elevate: false, out error) == 0 && !HasAnyStackrootRules())
        {
            error = null;
            InvalidateDisplayNameCache();
            return true;
        }

        if (allowElevation && RunPowerShell(script, elevate: true, out error) == 0 && !HasAnyStackrootRules())
        {
            error = null;
            InvalidateDisplayNameCache();
            return true;
        }

        if (HasAnyStackrootRules())
        {
            error ??= "Stackroot DNS routing rules are still active.";
            return false;
        }

        error ??= "Could not remove Stackroot DNS routing rules (NRPT).";
        return false;
    }

    private static bool IsInSync(IReadOnlyList<string> suffixes)
    {
        var desired = new HashSet<string>(
            suffixes.Select(BuildDisplayName),
            StringComparer.OrdinalIgnoreCase);
        var present = QueryPresentDisplayNames();
        if (desired.Count != present.Count)
        {
            return false;
        }

        foreach (var name in desired)
        {
            if (!present.Contains(name))
            {
                return false;
            }
        }

        return true;
    }

    private static HashSet<string> QueryPresentDisplayNames()
    {
        lock (DisplayNameCacheSync)
        {
            if (_cachedDisplayNames.Count > 0 && DateTimeOffset.UtcNow < _displayNamesExpireAt)
            {
                return _cachedDisplayNames;
            }
        }

        var script =
            "Get-DnsClientNrptRule -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -eq 'Stackroot .test DNS' -or $_.DisplayName -like 'Stackroot DNS*' } | ForEach-Object { $_.DisplayName }";
        if (RunPowerShell(script, elevate: false, out var output) != 0)
        {
            return [];
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (output ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(line, LegacyRuleDisplayName, StringComparison.OrdinalIgnoreCase))
            {
                names.Add(BuildDisplayName(".test"));
            }
            else
            {
                names.Add(line);
            }
        }

        lock (DisplayNameCacheSync)
        {
            _cachedDisplayNames = names;
            _displayNamesExpireAt = DateTimeOffset.UtcNow.Add(DisplayNameCacheTtl);
            return _cachedDisplayNames;
        }
    }

    private static void InvalidateDisplayNameCache()
    {
        lock (DisplayNameCacheSync)
        {
            _cachedDisplayNames = [];
            _displayNamesExpireAt = DateTimeOffset.MinValue;
        }
    }

    private static bool IsSuffixCovered(string suffix, HashSet<string> presentDisplayNames)
    {
        if (presentDisplayNames.Contains(BuildDisplayName(suffix)))
        {
            return true;
        }

        if (LocalDnsSuffix.IsCatchAllSuffix(suffix)
            && presentDisplayNames.Contains($"{RuleDisplayNamePrefix} (all names)"))
        {
            return true;
        }

        return string.Equals(suffix, ".test", StringComparison.OrdinalIgnoreCase)
               && presentDisplayNames.Contains(LegacyRuleDisplayName);
    }

    private static string BuildSyncScript(IReadOnlyList<string> suffixes)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "Get-DnsClientNrptRule -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -eq 'Stackroot .test DNS' -or $_.DisplayName -like 'Stackroot DNS*' } | Remove-DnsClientNrptRule -Force -ErrorAction SilentlyContinue");

        foreach (var suffix in suffixes)
        {
            var displayName = EscapeForPowerShell(BuildDisplayName(suffix));
            var escapedSuffix = EscapeForPowerShell(suffix);
            builder.AppendLine(
                $"Add-DnsClientNrptRule -Namespace '{escapedSuffix}' -NameServers '{NameServer}' -DisplayName '{displayName}' -ErrorAction Stop");
        }

        return builder.ToString();
    }

    private static string BuildDisplayName(string suffix) =>
        LocalDnsSuffix.IsCatchAllSuffix(suffix)
            ? $"{RuleDisplayNamePrefix} (all names)"
            : string.Equals(suffix, ".test", StringComparison.OrdinalIgnoreCase)
                ? $"{RuleDisplayNamePrefix} .test"
                : $"{RuleDisplayNamePrefix} {suffix}";

    private static string EscapeForPowerShell(string value) => value.Replace("'", "''");

    private static List<string> NormalizeSuffixes(IReadOnlyList<string> suffixes, bool ensureDefaultTest = true)
    {
        var allowDangerous = suffixes.Any(static suffix =>
            string.Equals(suffix?.Trim(), LocalDnsSuffix.CatchAllSuffix, StringComparison.Ordinal)
            || LocalDnsSuffix.IsCatchAllSuffix(suffix));
        return LocalDnsSuffix.NormalizeList(suffixes, ensureDefaultTest, allowDangerous);
    }

    private static int RunPowerShell(string script, bool elevate, out string? error)
    {
        error = null;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}";

        ProcessStartInfo psi;
        if (elevate)
        {
            psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true
            };
        }
        else
        {
            psi = ProcessStreamEncoding.Create("powershell.exe");
            psi.Arguments = arguments;
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                error = "Failed to start PowerShell.";
                return -1;
            }

            if (!elevate)
            {
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit(15000);
                if (process.ExitCode != 0)
                {
                    error = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
                }
                else
                {
                    error = stdout;
                }

                return process.ExitCode;
            }

            process.WaitForExit(30000);
            if (process.ExitCode != 0)
            {
                error = "Administrator approval was denied or the NRPT command failed.";
            }

            return process.ExitCode;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return -1;
        }
    }
}
