namespace Stackroot.Core.Abstractions.DataDocuments;

/// <summary>
/// Current schema version per Stackroot data document.
/// Bump when the on-disk JSON shape changes; add a migration step for each bump.
/// </summary>
public static class DataDocumentSchemas
{
    public const int Settings = 6;
    public const int Sites = 1;
    public const int Processes = 1;
    public const int Databases = 1;
    public const int Installed = 1;
    public const int ScheduledTasks = 2;
    public const int DownloadCache = 1;
    public const int SiteWpCredentials = 1;
    public const int SiteCustomCommands = 1;
    public const int PhpProfile = 1;
    public const int PhpProfilesBundle = 1;
}
