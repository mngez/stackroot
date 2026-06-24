using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Stackroot.Core.Dns;

public static class StackrootDnsServiceInstaller
{
    private static readonly Regex BinPathRegex = new(
        @"BINARY_PATH_NAME\s*:\s*(.+)$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly object ServiceStateSync = new();
    private static readonly TimeSpan ServiceStateCacheTtl = TimeSpan.FromSeconds(8);
    private static bool _serviceStateCached;
    private static bool _serviceInstalled;
    private static bool _serviceRunning;
    private static DateTimeOffset _serviceStateExpiresAt;

    public static void InvalidateServiceStateCache()
    {
        lock (ServiceStateSync)
        {
            _serviceStateCached = false;
        }
    }

    public static bool IsInstalled()
    {
        EnsureServiceStateCached();
        return _serviceInstalled;
    }

    public static bool IsRunning()
    {
        EnsureServiceStateCached();
        return _serviceRunning;
    }

    private static void EnsureServiceStateCached()
    {
        lock (ServiceStateSync)
        {
            if (_serviceStateCached && DateTimeOffset.UtcNow < _serviceStateExpiresAt)
            {
                return;
            }

            _serviceInstalled = TryQueryService(out var output);
            _serviceRunning = _serviceInstalled
                && output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
            _serviceStateCached = true;
            _serviceStateExpiresAt = DateTimeOffset.UtcNow.Add(ServiceStateCacheTtl);
        }
    }

    public static bool IsInstalledUncached() => TryQueryService(out _);

    public static bool IsRunningUncached()
    {
        if (!TryQueryService(out var output))
        {
            return false;
        }

        return output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryEnsureInstalled(string helperExePath, out string? error)
    {
        error = null;
        var fullPath = Path.GetFullPath(helperExePath);
        if (!File.Exists(fullPath))
        {
            error = $"DNS helper executable not found: {fullPath}";
            return false;
        }

        if (IsInstalled())
        {
            if (BinPathMatches(fullPath) && !IsConfiguredLegacyBinPath())
            {
                return TryEnsureAutomaticStart(out error);
            }

            var configuredExe = TryGetConfiguredHelperExePath();
            if (configuredExe is not null
                && DnsHelperBuildIdentity.FilesMatch(fullPath, configuredExe)
                && !IsConfiguredLegacyBinPath())
            {
                return TryEnsureAutomaticStart(out error);
            }

            var needsRecreate = IsConfiguredLegacyBinPath();
            if (!needsRecreate && !BinPathMatches(fullPath))
            {
                if (TryUpdateBinPath(fullPath, out error))
                {
                    return TryEnsureAutomaticStart(out error);
                }

                needsRecreate = true;
            }

            if (needsRecreate && !TryRecreateService(fullPath, out error))
            {
                return false;
            }

            if (!TryEnsureAutomaticStart(out error))
            {
                return false;
            }

            error = null;
            return true;
        }

        if (!TryCreateService(fullPath, out error))
        {
            error ??= "Could not register the Stackroot DNS Helper Windows service.";
            return false;
        }

        if (!TryEnsureAutomaticStart(out error))
        {
            return false;
        }

        InvalidateServiceStateCache();
        return IsInstalled();
    }

    public static string? TryGetConfiguredHelperExePath()
    {
        if (!TryQueryServiceConfig(out var output))
        {
            return null;
        }

        var match = BinPathRegex.Match(output);
        if (!match.Success)
        {
            return null;
        }

        var configured = match.Groups[1].Value.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(configured);
        }
        catch
        {
            return null;
        }
    }

    public static bool TryStart(out string? error)
    {
        if (IsRunning())
        {
            error = null;
            return true;
        }

        if (IsAutomaticStartType())
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                if (IsRunningUncached())
                {
                    InvalidateServiceStateCache();
                    error = null;
                    return true;
                }

                Thread.Sleep(300);
            }
        }

        if (IsRunning())
        {
            error = null;
            return true;
        }

        if (TryRunSc($"start {StackrootDnsHelperConstants.ServiceName}", elevate: false, out error)
            || TryRunSc($"start {StackrootDnsHelperConstants.ServiceName}", elevate: true, out error))
        {
            InvalidateServiceStateCache();
            if (IsRunning())
            {
                error = null;
                return true;
            }
        }

        error = DescribeStartFailure(error);
        return false;
    }

    public static bool TryStop(out string? error)
    {
        if (!IsInstalled() || !IsRunning())
        {
            error = null;
            return true;
        }

        if (TryRunSc($"stop {StackrootDnsHelperConstants.ServiceName}", elevate: false, out error)
            || TryRunSc($"stop {StackrootDnsHelperConstants.ServiceName}", elevate: true, out error))
        {
            InvalidateServiceStateCache();
            error = null;
            return !IsRunning();
        }

        error ??= "Could not stop the Stackroot DNS Helper service.";
        return false;
    }

    public static bool TryUninstall(out string? error)
    {
        if (!IsInstalled())
        {
            error = null;
            return true;
        }

        TryStop(out _);
        var deleteArgs = $"delete {StackrootDnsHelperConstants.ServiceName}";
        if (TryRunSc(deleteArgs, elevate: false, out error)
            || TryRunSc(deleteArgs, elevate: true, out error))
        {
            error = null;
            return !IsInstalled();
        }

        error ??= "Could not remove the Stackroot DNS Helper Windows service.";
        return false;
    }

    public static string ResolveHelperExePath() =>
        StackrootDnsHelperLayout.ResolveStableHelperExePath(AppContext.BaseDirectory);

    private static string FormatBinPath(string helperExePath) => $"\\\"{Path.GetFullPath(helperExePath)}\\\"";

    private static bool BinPathMatches(string helperExePath)
    {
        if (!TryQueryServiceConfig(out var output))
        {
            return false;
        }

        var match = BinPathRegex.Match(output);
        if (!match.Success)
        {
            return false;
        }

        var configured = match.Groups[1].Value.Trim().Trim('"');
        return string.Equals(
            Path.GetFullPath(configured),
            Path.GetFullPath(helperExePath),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsConfiguredLegacyBinPath()
    {
        if (!TryQueryServiceConfig(out var output))
        {
            return false;
        }

        var match = BinPathRegex.Match(output);
        if (!match.Success)
        {
            return false;
        }

        return StackrootDnsHelperLayout.IsLegacyHelperBinPath(match.Groups[1].Value.Trim().Trim('"'));
    }

    private static bool TryCreateService(string helperExePath, out string? error)
    {
        var batch = BuildCreateServiceBatch(helperExePath);
        if (TryRunElevatedBatch(batch, elevate: false, out error)
            || TryRunElevatedBatch(batch, elevate: true, out error))
        {
            error = null;
            InvalidateServiceStateCache();
            return true;
        }

        return false;
    }

    private static bool TryRecreateService(string helperExePath, out string? error)
    {
        var name = StackrootDnsHelperConstants.ServiceName;
        var batch =
            $"sc stop {name} & sc delete {name} & {BuildCreateServiceBatch(helperExePath)}";
        if (TryRunElevatedBatch(batch, elevate: false, out error)
            || TryRunElevatedBatch(batch, elevate: true, out error))
        {
            error = null;
            InvalidateServiceStateCache();
            return BinPathMatches(helperExePath);
        }

        return false;
    }

    private static string BuildCreateServiceBatch(string helperExePath)
    {
        var binPath = FormatBinPath(helperExePath);
        var name = StackrootDnsHelperConstants.ServiceName;
        return
            $"sc create {name} binPath= {binPath} start= auto DisplayName= \"{StackrootDnsHelperConstants.ServiceDisplayName}\" obj= LocalSystem & sc description {name} \"{StackrootDnsHelperConstants.ServiceDescription}\"";
    }

    /// <summary>
    /// Ensures the helper starts with Windows immediately (not delayed-auto).
    /// Upgrades legacy delayed-auto registrations on existing machines.
    /// </summary>
    public static bool TryEnsureAutomaticStart(out string? error)
    {
        error = null;
        if (!IsInstalled() || IsImmediateAutomaticStart())
        {
            return true;
        }

        var args = $"config {StackrootDnsHelperConstants.ServiceName} start= auto";
        if (TryRunSc(args, elevate: false, out error) || TryRunSc(args, elevate: true, out error))
        {
            error = null;
            return IsImmediateAutomaticStart();
        }

        error ??= "Could not configure the Stackroot DNS Helper service to start automatically with Windows.";
        return false;
    }

    private static bool IsAutomaticStartType() =>
        IsImmediateAutomaticStart() || IsDelayedAutomaticStart();

    private static bool IsImmediateAutomaticStart()
    {
        if (!TryQueryServiceConfig(out var output))
        {
            return false;
        }

        return output.Contains("AUTO_START", StringComparison.OrdinalIgnoreCase)
            && !output.Contains("DELAYED", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDelayedAutomaticStart()
    {
        if (!TryQueryServiceConfig(out var output))
        {
            return false;
        }

        return output.Contains("DELAYED", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryUpdateBinPath(string helperExePath, out string? error)
    {
        var binPath = FormatBinPath(helperExePath);
        var args = $"config {StackrootDnsHelperConstants.ServiceName} binPath= {binPath}";
        if (TryRunSc(args, elevate: false, out error) || TryRunSc(args, elevate: true, out error))
        {
            error = null;
            InvalidateServiceStateCache();
            return BinPathMatches(helperExePath);
        }

        error ??= "Could not update the Stackroot DNS Helper service path.";
        return false;
    }

    private static string DescribeStartFailure(string? priorError)
    {
        if (TryQueryService(out var output))
        {
            if (output.Contains("WIN32_EXIT_CODE    : 1067", StringComparison.Ordinal))
            {
                return "Stackroot DNS Helper exited immediately (1067). Reinstall the app or run Stackroot as administrator once to repair the service.";
            }

            if (output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)
                && output.Contains("1063", StringComparison.Ordinal))
            {
                return "Stackroot DNS Helper is not registered correctly for Windows service startup.";
            }
        }

        return string.IsNullOrWhiteSpace(priorError)
            ? "Could not start the Stackroot DNS Helper service."
            : priorError;
    }

    private static bool TryQueryService(out string output) =>
        TryRunScQuery($"query {StackrootDnsHelperConstants.ServiceName}", out output);

    private static bool TryQueryServiceConfig(out string output) =>
        TryRunScQuery($"qc {StackrootDnsHelperConstants.ServiceName}", out output);

    private static bool TryRunScQuery(string arguments, out string output)
    {
        output = string.Empty;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryRunElevatedBatch(string batchCommands, bool elevate, out string? error)
    {
        error = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {batchCommands}",
                UseShellExecute = elevate,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            if (elevate)
            {
                psi.Verb = "runas";
            }
            else
            {
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.UseShellExecute = false;
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                error = "Failed to start the service registration command.";
                return false;
            }

            if (!elevate)
            {
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit(30000);
                if (process.ExitCode != 0)
                {
                    error = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
                    return false;
                }

                return true;
            }

            process.WaitForExit(60000);
            if (process.ExitCode != 0)
            {
                error = "The service registration command failed. Try again or run Stackroot as administrator once.";
                return false;
            }

            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error =
                "Windows administrator approval was cancelled. Stackroot needs permission once to register or repair the DNS helper service.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryRunSc(string arguments, bool elevate, out string? error)
    {
        error = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = arguments,
                UseShellExecute = elevate,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            if (elevate)
            {
                psi.Verb = "runas";
            }
            else
            {
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.UseShellExecute = false;
            }

            using var process = Process.Start(psi);
            if (process is null)
            {
                error = "Failed to start sc.exe.";
                return false;
            }

            if (!elevate)
            {
                var stdout = process.StandardOutput.ReadToEnd();
                var stderr = process.StandardError.ReadToEnd();
                process.WaitForExit(15000);
                if (process.ExitCode != 0)
                {
                    error = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
                    return false;
                }

                return true;
            }

            process.WaitForExit(30000);
            if (process.ExitCode != 0)
            {
                error = "The service command failed. Try again or run Stackroot as administrator once.";
                return false;
            }

            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error =
                "Windows administrator approval was cancelled. Stackroot needs permission once to manage the DNS helper service.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
