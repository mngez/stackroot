namespace Stackroot.Core.Dns;

public static class StackrootDnsHelperConstants
{
    public const string ServiceName = "StackrootDnsHelper";
    public const string ServiceDisplayName = "Stackroot DNS Helper";
    public const string ServiceDescription =
        "Stackroot local dev DNS (127.0.0.1:53 and Windows NRPT routing for configured suffixes).";

    public const string ConfigFileName = "dns-helper.json";
    public const string StatusFileName = "dns-helper-status.json";

    public static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Stackroot");

    public static string ConfigPath => Path.Combine(ConfigDirectory, ConfigFileName);

    public static string StatusPath => Path.Combine(ConfigDirectory, StatusFileName);

    public const string HelperExeName = "StackrootDnsHelper.exe";
}
