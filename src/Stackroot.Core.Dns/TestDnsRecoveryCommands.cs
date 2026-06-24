namespace Stackroot.Core.Dns;

public static class TestDnsRecoveryCommands
{
    private const string RemoveNrptRulesScript =
        "Get-DnsClientNrptRule -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -eq 'Stackroot .test DNS' -or $_.DisplayName -like 'Stackroot DNS*' } | Remove-DnsClientNrptRule -Force -ErrorAction SilentlyContinue";

    /// <summary>
    /// One-liner for elevated PowerShell: stops the helper service and removes Stackroot NRPT rules.
    /// </summary>
    public static string WindowsCleanupScript =>
        "$ErrorActionPreference = 'SilentlyContinue'; " +
        $"sc.exe stop {StackrootDnsHelperConstants.ServiceName}; " +
        $"sc.exe delete {StackrootDnsHelperConstants.ServiceName}; " +
        RemoveNrptRulesScript;
}
