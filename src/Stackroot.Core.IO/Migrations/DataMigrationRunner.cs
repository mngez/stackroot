using System.Text.Json.Nodes;
using Stackroot.Core.Abstractions;

namespace Stackroot.Core.IO.Migrations;

public static class DataMigrationRunner
{
    private static readonly JsonDocumentMigrator[] RegistryMigrators =
    [
        new Migrators.SitesJsonMigrator(),
        new Migrators.ProcessesJsonMigrator(),
        new Migrators.DatabasesJsonMigrator(),
        new Migrators.InstalledJsonMigrator(),
        new Migrators.ScheduledTasksJsonMigrator()
    ];

    private static readonly JsonDocumentMigrator[] SiteMigrators =
    [
        new Migrators.WpCredentialsJsonMigrator(),
        new Migrators.CustomCommandsJsonMigrator()
    ];

    public static DataMigrationReport Run(StackrootPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var report = new DataMigrationReport();
        var context = new DataMigrationContext();

        new Migrators.SettingsJsonMigrator().MigrateAll(paths, context, report);

        context.DownloadCacheRoot = ResolveDownloadCacheRoot(paths);
        foreach (var migrator in RegistryMigrators)
        {
            migrator.MigrateAll(paths, context, report);
        }

        new Migrators.DownloadsJsonMigrator().MigrateAll(paths, context, report);

        foreach (var migrator in SiteMigrators)
        {
            migrator.MigrateAll(paths, context, report);
        }

        return report;
    }

    private static string ResolveDownloadCacheRoot(StackrootPaths paths)
    {
        var settingsPath = StackrootPathResolver.SettingsPath(paths.DataRoot);
        if (File.Exists(settingsPath))
        {
            try
            {
                var root = JsonMigrationHelper.ParseOrNull(settingsPath);
                if (root is JsonObject obj
                    && obj["general"] is JsonObject general
                    && general["downloadCachePath"] is JsonValue value
                    && value.TryGetValue<string>(out var configured)
                    && !string.IsNullOrWhiteSpace(configured))
                {
                    return Path.GetFullPath(configured);
                }
            }
            catch
            {
            }
        }

        return ResolveDownloadCacheRoot(paths.DataRoot);
    }

    private static string ResolveDownloadCacheRoot(string dataRoot)
    {
        var fromEnv = Environment.GetEnvironmentVariable("STACKROOT_DOWNLOAD_CACHE");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        var testEnv = Environment.GetEnvironmentVariable("STACKROOT_TEST_CACHE");
        if (!string.IsNullOrWhiteSpace(testEnv))
        {
            return Path.GetFullPath(testEnv);
        }

        return Path.Combine(dataRoot, "downloads");
    }
}

internal static class JsonDocumentMigratorExtensions
{
    public static void MigrateAll(
        this JsonDocumentMigrator migrator,
        StackrootPaths paths,
        DataMigrationContext context,
        DataMigrationReport report)
    {
        foreach (var path in migrator.ResolvePaths(paths, context))
        {
            migrator.MigrateFile(path, report);
        }
    }
}
