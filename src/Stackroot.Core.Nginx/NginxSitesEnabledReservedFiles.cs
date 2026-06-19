namespace Stackroot.Core.Nginx;

/// <summary>
/// Nginx vhost files in sites-enabled that are not site IDs and must not be removed by site regeneration.
/// </summary>
public static class NginxSitesEnabledReservedFiles
{
    private static readonly HashSet<string> ConfFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "stackroot-app.conf",
        "stackroot-phpmyadmin.conf",
        "stackroot-phpredisadmin.conf",
    };

    public static bool IsReserved(string fileName) => ConfFileNames.Contains(fileName);
}
