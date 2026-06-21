using System.Diagnostics;
using System.Text;

namespace Stackroot.Core.Windows.Dns;

/// <summary>
/// Routes <c>.test</c> DNS queries to the local Stackroot resolver via Windows NRPT.
/// </summary>
public sealed class WindowsNrptManager
{
    public const string RuleDisplayName = "Stackroot .test DNS";
    public const string RuleNamespace = ".test";
    public const string NameServer = "127.0.0.1";

    public bool IsRulePresent()
    {
        var script = $"$r = Get-DnsClientNrptRule -ErrorAction SilentlyContinue | Where-Object {{ $_.DisplayName -eq '{RuleDisplayName}' }}; if ($r) {{ 'yes' }}";
        return RunPowerShell(script, elevate: false, out var output) == 0 &&
               (output?.Contains("yes", StringComparison.Ordinal) ?? false);
    }

    public bool TryEnable(out string? error)
    {
        if (IsRulePresent())
        {
            error = null;
            return true;
        }

        var script =
            $"Add-DnsClientNrptRule -Namespace '{RuleNamespace}' -NameServers '{NameServer}' -DisplayName '{RuleDisplayName}' -ErrorAction Stop";
        if (RunPowerShell(script, elevate: false, out error) == 0)
        {
            error = null;
            return true;
        }

        if (RunPowerShell(script, elevate: true, out error) == 0)
        {
            error = null;
            return true;
        }

        error ??= "Could not register the .test DNS routing rule (NRPT).";
        return false;
    }

    public bool TryDisable(out string? error)
    {
        if (!IsRulePresent())
        {
            error = null;
            return true;
        }

        var script =
            $"Get-DnsClientNrptRule -ErrorAction SilentlyContinue | Where-Object {{ $_.DisplayName -eq '{RuleDisplayName}' }} | Remove-DnsClientNrptRule -Force -ErrorAction SilentlyContinue";
        if (RunPowerShell(script, elevate: false, out error) == 0)
        {
            error = null;
            return true;
        }

        if (RunPowerShell(script, elevate: true, out error) == 0)
        {
            error = null;
            return true;
        }

        error ??= "Could not remove the .test DNS routing rule (NRPT).";
        return false;
    }

    private static int RunPowerShell(string script, bool elevate, out string? error)
    {
        error = null;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (elevate)
        {
            psi.UseShellExecute = true;
            psi.RedirectStandardOutput = false;
            psi.RedirectStandardError = false;
            psi.Verb = "runas";
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
            return process.ExitCode;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return -1;
        }
    }
}
