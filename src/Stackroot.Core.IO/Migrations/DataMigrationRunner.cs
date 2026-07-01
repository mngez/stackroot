using System.Text.Json.Nodes;
using Stackroot.Core.Abstractions;

namespace Stackroot.Core.IO.Migrations;

public static class DataMigrationRunner
{
    private static volatile bool _completedThisProcess;

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

    public static DataMigrationReport Run(StackrootPaths paths, bool allowRepeat = true)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (!allowRepeat && _completedThisProcess)
        {
            return new DataMigrationReport();
        }

        var report = RunCore(paths);
        _completedThisProcess = true;
        return report;
    }

    private static DataMigrationReport RunCore(StackrootPaths paths)
    {
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

        MigrateBackupFiles(paths);

        return report;
    }

    private static void MigrateBackupFiles(StackrootPaths paths)
    {
        // 0.2.9 → 0.3.0: move flat *.sql / *.archive files from {dataRoot}/backups/
        // into {backupsRoot}/databases/ subfolder.
        var oldDir = Path.Combine(paths.DataRoot, "backups");
        if (!Directory.Exists(oldDir))
        {
            return;
        }

        var backupsRoot = ResolveBackupsRoot(paths);
        var newDir = Path.Combine(backupsRoot, "databases");

        var filesToMove = Directory
            .EnumerateFiles(oldDir, "*.sql", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(oldDir, "*.archive", SearchOption.TopDirectoryOnly))
            .ToList();

        if (filesToMove.Count == 0)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(newDir);
            foreach (var file in filesToMove)
            {
                var dest = Path.Combine(newDir, Path.GetFileName(file));
                if (!File.Exists(dest))
                {
                    File.Move(file, dest);
                }
            }
        }
        catch (Exception ex)
        {
            // Non-fatal — existing backups stay in place and ListBackups will still find them
            try
            {
                var logPath = Path.Combine(paths.DataRoot, "logs", "app-error.log");
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.AppendAllText(logPath,
                    $"[{DateTimeOffset.UtcNow:O}] [WARN] MigrateBackupFiles failed: {ex}\n");
            }
            catch { }
        }
    }

    private static string ResolveBackupsRoot(StackrootPaths paths)
    {
        var settingsPath = StackrootPathResolver.SettingsPath(paths.DataRoot);
        if (File.Exists(settingsPath))
        {
            try
            {
                var root = JsonMigrationHelper.ParseOrNull(settingsPath);
                if (root is JsonObject obj
                    && obj["general"] is JsonObject general
                    && general["backupsPath"] is JsonValue value
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

        return Path.Combine(paths.DataRoot, "backups");
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
