using Stackroot.Core.Abstractions;

namespace Stackroot.Core.IO;

public static class StackrootPathResolver
{
    private const string AppName = "Stackroot";

    public static string DefaultDataRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);

    public static string DefaultRuntimeRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName, "runtime");

    public static string LegacyRuntimeRoot =>
        Path.Combine(DefaultDataRoot, "runtime");

    public static StackrootPaths Resolve(StackrootPaths? overrides = null, bool ensureDirectories = true)
    {
        var dataRoot = string.IsNullOrWhiteSpace(overrides?.DataRoot)
            ? DefaultDataRoot
            : overrides.DataRoot;

        var paths = new StackrootPaths
        {
            DataRoot = dataRoot,
            RuntimeRoot = OrDefault(overrides?.RuntimeRoot, DefaultRuntimeRoot),
            ResourcesRoot = OrDefault(overrides?.ResourcesRoot, Path.Combine(dataRoot, "resources")),
            SitesRoot = OrDefault(overrides?.SitesRoot, Path.Combine(dataRoot, "sites")),
            ConfigRoot = OrDefault(overrides?.ConfigRoot, Path.Combine(dataRoot, "config")),
            LogsRoot = OrDefault(overrides?.LogsRoot, Path.Combine(dataRoot, "logs"))
        };

        if (ensureDirectories)
        {
            Directory.CreateDirectory(paths.DataRoot);
            Directory.CreateDirectory(paths.RuntimeRoot);
            Directory.CreateDirectory(paths.ResourcesRoot);
            Directory.CreateDirectory(paths.SitesRoot);
            Directory.CreateDirectory(paths.ConfigRoot);
            Directory.CreateDirectory(paths.LogsRoot);
        }

        return paths;
    }

    public static string CatalogPath(string resourcesRoot) => Path.Combine(resourcesRoot, "packages", "catalog.json");

    public static string RegistryPath(string dataRoot) => Path.Combine(dataRoot, "installed.json");

    public static string SettingsPath(string dataRoot) => Path.Combine(dataRoot, "settings.json");

    public static string SitesRegistryPath(string dataRoot) => Path.Combine(dataRoot, "sites.json");

    public static string ProcessesRegistryPath(string dataRoot) => Path.Combine(dataRoot, "processes.json");

    public static string DatabasesRegistryPath(string dataRoot) => Path.Combine(dataRoot, "databases.json");

    public static string DefaultBackupsRoot(string dataRoot) => Path.Combine(dataRoot, "backups");

    public static string DatabaseBackupsPath(string dataRoot) => Path.Combine(dataRoot, "backups");

    public static string SiteBackupsPath(string dataRoot) => Path.Combine(dataRoot, "backups", "sites");

    public static string SiteBackupsDir(string backupsRoot) => Path.Combine(backupsRoot, "sites");

    public static string DatabaseBackupsDir(string backupsRoot) => Path.Combine(backupsRoot, "databases");

    public static string DownloadsPath(string dataRoot) => Path.Combine(dataRoot, "downloads");

    public static string DownloadsRegistryPath(string cacheRoot) => Path.Combine(cacheRoot, "downloads.json");

    public static string ScheduledTasksPath(string dataRoot) => Path.Combine(dataRoot, "scheduled-tasks.json");

    public static string SiteWpCredentialsPath(string siteDataDir) => Path.Combine(siteDataDir, "wp-credentials.json");

    public static string SiteCustomCommandsPath(string siteDataDir) => Path.Combine(siteDataDir, "custom-commands.json");

    public static string SiteCustomCommandIconsPath(string siteDataDir) => Path.Combine(siteDataDir, "custom-command-icons");

    public static string InstalledMarkerPath(string dataRoot) => FirstRunState.InstalledMarkerPath(dataRoot);

    public static string RuntimeMigrationMarkerPath(string dataRoot) =>
        Path.Combine(dataRoot, "runtime-local-migration.done");

    private static string OrDefault(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
