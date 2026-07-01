using System.IO.Compression;
using Stackroot.Core.IO;
using Stackroot.Core.Settings;
using Stackroot.Core.Sites.Management;
using Stackroot.Core.Sites.Models;
using Stackroot.Core.Supervisor;
using Stackroot.App.Scheduling;
using StackrootPaths = Stackroot.Core.Abstractions.StackrootPaths;

namespace Stackroot.App.Services;

public sealed record BackupResult(
    bool Success,
    string? ResultPath,
    bool SiteDeleted,
    string SiteDomain,
    string? ErrorMessage,
    Exception? Exception);

/// <summary>
/// Shared backup execution logic used by both SitesViewModel and SiteManageViewModel.
/// Eliminates duplication of the core backup flow (Begin → CreateBackup → handle delete → End).
/// </summary>
public static class SiteBackupHelper
{
    public static async Task<BackupResult> RunBackupAsync(
        Site site,
        SiteBackupOptions options,
        SiteBackupService backupService,
        SiteBackupTracker backupTracker,
        SessionActivityReporter activity,
        SiteManager siteManager,
        GlobalProcessManager processManager,
        TaskSchedulerService scheduler,
        SettingsStore settingsStore,
        StackrootPaths paths,
        CancellationToken cancellationToken = default)
    {
        var settings = settingsStore.Load();
        var backupsRoot = !string.IsNullOrWhiteSpace(settings.General.BackupsPath)
            ? settings.General.BackupsPath
            : StackrootPathResolver.DefaultBackupsRoot(paths.DataRoot);
        var destDir = StackrootPathResolver.SiteBackupsDir(backupsRoot);

        backupTracker.Begin(site.Id, site.Domain);
        var progressId = activity.Begin("Backup", $"Backing up {site.Domain}…");

        try
        {
            var progress = new Progress<string>(msg => activity.UpdateProgress(progressId, "Backup", msg));
            var resultPath = await backupService.CreateBackupAsync(site, destDir, options, progress, cancellationToken);

            if (options.DeleteSiteAfterBackup)
            {
                using (var zip = ZipFile.OpenRead(resultPath))
                {
                    if (zip.Entries.Count == 0)
                        throw new InvalidOperationException("Backup archive is empty or corrupt. Site deletion cancelled.");
                }
                foreach (var process in processManager.List(site.Id))
                    processManager.Remove(process.Id);

                scheduler.DeleteBySiteId(site.Id);
                siteManager.Delete(site.Id, forceDeleteFiles: true, deleteDatabases: true);
            }

            backupTracker.End(site.Id);
            activity.Complete(progressId, "Backup", $"{site.Domain} backed up successfully");

            return new BackupResult(
                Success: true,
                ResultPath: resultPath,
                SiteDeleted: options.DeleteSiteAfterBackup,
                SiteDomain: site.Domain,
                ErrorMessage: null,
                Exception: null);
        }
        catch (Exception ex)
        {
            backupTracker.End(site.Id);
            activity.Fail(progressId, "Backup", $"Backup failed for {site.Domain}: {ex.Message}", ex);
            return new BackupResult(
                Success: false,
                ResultPath: null,
                SiteDeleted: false,
                SiteDomain: site.Domain,
                ErrorMessage: ex.Message,
                Exception: ex);
        }
    }
}
