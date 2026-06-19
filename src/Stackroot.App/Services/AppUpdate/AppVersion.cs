using System.Reflection;

namespace Stackroot.App.Services.AppUpdate;

public static class AppVersion
{
    public static string Current =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public static bool TryParse(string? value, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().TrimStart('v', 'V');
        return Version.TryParse(normalized, out version!);
    }

    public static bool IsRemoteNewer(string remoteVersion, string currentVersion)
    {
        if (!TryParse(remoteVersion, out var remote) || !TryParse(currentVersion, out var current))
        {
            return false;
        }

        return remote > current;
    }
}
