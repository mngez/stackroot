using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stackroot.App.Scheduling;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Databases;
using Stackroot.Core.IO;
using Stackroot.Core.Sites.Management;
using Stackroot.Core.Sites.Models;
using Stackroot.Core.Supervisor;
using CreateSiteInput = Stackroot.Core.Sites.Models.CreateSiteInput;
using SiteModel = Stackroot.Core.Sites.Models.Site;
using UpdateSiteInput = Stackroot.Core.Sites.Models.UpdateSiteInput;

namespace Stackroot.App.Services;

public sealed record BackupManifest(
    string SchemaVersion,
    string BackupDate,
    string SiteName,
    string SiteDomain,
    string SiteId,
    BackupManifestContents Contents,
    IReadOnlyList<BackupManifestDatabase> Databases);

public sealed record BackupManifestContents(
    bool HasFiles,
    bool HasDatabases,
    bool HasProcesses,
    bool HasScheduledTasks);

public sealed record BackupManifestDatabase(
    string Name,
    string Engine,
    string File);

public sealed record SiteImportConflict(
    bool PrimaryDomainExists,
    IReadOnlyList<string> AllAliases,
    IReadOnlyList<string> ConflictingAliases,
    IReadOnlyList<string> ConflictingDatabaseNames);

public sealed record SiteRestoreOptions(
    bool RestoreFiles,
    bool RestoreDatabases,
    bool RestoreProcesses,
    bool RestoreScheduledTasks,
    bool SkipFileSafetyCopy = false,
    bool SkipDbSafetyCopy = false,
    IReadOnlyList<string>? DatabaseNamesToRestore = null);

public sealed record SiteBackupEntry(
    string FilePath,
    string FileName,
    long SizeBytes,
    DateTimeOffset Date,
    BackupManifest Manifest);

// --- Delta / Rollback models ---

public enum DeltaItemAction { Restore, Replace, Delete }

public sealed record DeltaDatabaseItem(string Name, string Engine, DeltaItemAction Action);
public sealed record DeltaProcessItem(string ProcessId, string Name, DeltaItemAction Action);
public sealed record DeltaTaskItem(string TaskId, string Label, DeltaItemAction Action);

public sealed class SiteRestoreDelta
{
    public bool HasBackupFiles { get; init; }
    public bool HasBackupDatabases { get; init; }
    public bool HasBackupProcesses { get; init; }
    public bool HasBackupScheduledTasks { get; init; }
    public IReadOnlyList<DeltaDatabaseItem> Databases { get; init; } = [];
    public IReadOnlyList<DeltaProcessItem> Processes { get; init; } = [];
    public IReadOnlyList<DeltaTaskItem> ScheduledTasks { get; init; } = [];
}

public sealed record SiteRollbackDeletions(
    IReadOnlyList<string> DatabaseNamesToDelete,
    IReadOnlyList<string> ProcessIdsToDelete,
    IReadOnlyList<string> TaskIdsToDelete);

public sealed class SiteRestoreService
{
    private readonly DatabaseManager _databaseManager;
    private readonly GlobalProcessManager _processManager;
    private readonly TaskSchedulerService _taskScheduler;
    private readonly SiteManager _siteManager;
    private readonly StackrootPaths _paths;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SiteRestoreService(
        DatabaseManager databaseManager,
        GlobalProcessManager processManager,
        TaskSchedulerService taskScheduler,
        SiteManager siteManager,
        StackrootPaths paths)
    {
        _databaseManager = databaseManager;
        _processManager = processManager;
        _taskScheduler = taskScheduler;
        _siteManager = siteManager;
        _paths = paths;
    }

    public (long ExtractedBytes, long DbBackupBytes) EstimateRestoreSpace(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        var dbFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var manifestEntry = archive.GetEntry("manifest.json");
        if (manifestEntry is not null)
        {
            try
            {
                using var ms = new MemoryStream();
                using (var s = manifestEntry.Open()) s.CopyTo(ms);
                var node = JsonNode.Parse(Encoding.UTF8.GetString(ms.ToArray())) as JsonObject;
                if (node is not null)
                {
                    var manifest = ParseManifest(node);
                    foreach (var db in manifest.Databases)
                        dbFiles.Add(db.File.Replace('\\', '/'));
                }
            }
            catch { }
        }

        long extractedBytes = 0, dbBytes = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.Length <= 0) continue;
            extractedBytes += entry.Length;
            if (dbFiles.Contains(entry.FullName.Replace('\\', '/')))
                dbBytes += entry.Length;
        }

        return (extractedBytes, dbBytes);
    }

    public BackupManifest ReadManifest(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("ZIP does not contain a manifest.json.");

        using var stream = entry.Open();
        var node = JsonNode.Parse(stream) as JsonObject
            ?? throw new InvalidDataException("manifest.json is not a valid JSON object.");

        return ParseManifest(node);
    }

    public IReadOnlyList<SiteBackupEntry> ListSiteBackups(string siteId, string backupDirectory)
    {
        if (!Directory.Exists(backupDirectory)) return [];

        var results = new List<SiteBackupEntry>();
        foreach (var file in Directory.EnumerateFiles(backupDirectory, "*.zip"))
        {
            try
            {
                var manifest = ReadManifest(file);
                if (!string.Equals(manifest.SiteId, siteId, StringComparison.OrdinalIgnoreCase)) continue;
                var info = new FileInfo(file);
                DateTimeOffset.TryParse(manifest.BackupDate, out var date);
                results.Add(new SiteBackupEntry(file, Path.GetFileName(file), info.Length, date, manifest));
            }
            catch { /* skip corrupt or unrelated ZIPs */ }
        }

        return results.OrderByDescending(e => e.Date).ToList();
    }

    public SiteImportConflict CheckConflicts(BackupManifest manifest, string zipPath)
    {
        // Read site.json from ZIP to get DomainAliases
        List<string> allAliases = [];
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var siteEntry = archive.GetEntry("site.json");
            if (siteEntry is not null)
            {
                using var stream = siteEntry.Open();
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var siteJson = Encoding.UTF8.GetString(ms.ToArray());
                var originalSite = JsonSerializer.Deserialize<SiteModel>(siteJson, JsonOpts);
                allAliases = originalSite?.DomainAliases ?? [];
            }
        }
        catch { /* treat as no aliases */ }

        // Build set of ALL domain names in use across all existing sites (primary + aliases)
        var allExistingDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var site in _siteManager.List())
        {
            allExistingDomains.Add(site.Domain);
            foreach (var alias in site.DomainAliases ?? [])
                allExistingDomains.Add(alias);
        }

        var primaryConflict = allExistingDomains.Contains(manifest.SiteDomain);
        var conflictingAliases = allAliases
            .Where(a => allExistingDomains.Contains(a))
            .ToList();

        var existingDbNames = _databaseManager.List()
            .Select(db => db.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var conflictingDbs = manifest.Databases
            .Where(db => existingDbNames.Contains(db.Name))
            .Select(db => db.Name)
            .ToList();

        return new SiteImportConflict(primaryConflict, allAliases, conflictingAliases, conflictingDbs);
    }

    public SiteRestoreDelta ComputeRestoreDelta(string zipPath, SiteModel site)
    {
        var dbItems = new List<DeltaDatabaseItem>();
        var processItems = new List<DeltaProcessItem>();
        var taskItems = new List<DeltaTaskItem>();

        // Open the ZIP once; manifest read is required so exceptions propagate.
        using var archive = ZipFile.OpenRead(zipPath);

        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidDataException("ZIP does not contain a manifest.json.");
        BackupManifest manifest;
        using (var ms = new MemoryStream())
        {
            using (var s = manifestEntry.Open()) s.CopyTo(ms);
            var node = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(ms.ToArray())) as JsonObject
                ?? throw new InvalidDataException("manifest.json is not a valid JSON object.");
            manifest = ParseManifest(node);
        }

        // Databases
        var currentDbs = _databaseManager.List()
            .Where(d => string.Equals(d.SiteId, site.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var currentDbNames = currentDbs.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var backupDbNames = manifest.Databases.Select(d => d.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var db in manifest.Databases)
        {
            var action = currentDbNames.Contains(db.Name) ? DeltaItemAction.Replace : DeltaItemAction.Restore;
            dbItems.Add(new DeltaDatabaseItem(db.Name, db.Engine, action));
        }
        foreach (var db in currentDbs.Where(d => !backupDbNames.Contains(d.Name)))
            dbItems.Add(new DeltaDatabaseItem(db.Name, db.Engine.ToString().ToLowerInvariant(), DeltaItemAction.Delete));

        // Processes and tasks — read directly from ZIP entries (no full extract)
        try
        {
            if (manifest.Contents.HasProcesses)
            {
                var currentProcesses = _processManager.List(site.Id).ToList();
                var currentProcessIds = currentProcesses.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);

                var processEntry = archive.GetEntry("processes.json");
                if (processEntry is not null)
                {
                    var backupProcesses = ReadProcessesFromEntry(processEntry);
                    var backupProcessIds = backupProcesses.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);

                    foreach (var p in backupProcesses)
                    {
                        var action = currentProcessIds.Contains(p.Id) ? DeltaItemAction.Replace : DeltaItemAction.Restore;
                        processItems.Add(new DeltaProcessItem(p.Id, p.Name, action));
                    }
                    foreach (var p in currentProcesses.Where(p => !backupProcessIds.Contains(p.Id)))
                        processItems.Add(new DeltaProcessItem(p.Id, p.Name, DeltaItemAction.Delete));
                }
            }
            else
            {
                // Backup was created without processes — all current processes will be cleared on restore
                foreach (var p in _processManager.List(site.Id))
                    processItems.Add(new DeltaProcessItem(p.Id, p.Name, DeltaItemAction.Delete));
            }

            if (manifest.Contents.HasScheduledTasks)
            {
                var currentTasks = _taskScheduler.List()
                    .Where(t => string.Equals(t.SiteId, site.Id, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var currentTaskIds = currentTasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);

                var taskEntry = archive.GetEntry("scheduled-tasks.json");
                if (taskEntry is not null)
                {
                    var backupTasks = ReadTasksFromEntry(taskEntry);
                    var backupTaskIds = backupTasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);

                    foreach (var t in backupTasks)
                    {
                        var action = currentTaskIds.Contains(t.Id) ? DeltaItemAction.Replace : DeltaItemAction.Restore;
                        taskItems.Add(new DeltaTaskItem(t.Id, t.Label, action));
                    }
                    foreach (var t in currentTasks.Where(t => !backupTaskIds.Contains(t.Id)))
                        taskItems.Add(new DeltaTaskItem(t.Id, t.Label, DeltaItemAction.Delete));
                }
            }
            else
            {
                // Backup was created without tasks — all current tasks will be cleared on restore
                foreach (var t in _taskScheduler.List()
                    .Where(t => string.Equals(t.SiteId, site.Id, StringComparison.OrdinalIgnoreCase)))
                    taskItems.Add(new DeltaTaskItem(t.Id, t.Label, DeltaItemAction.Delete));
            }
        }
        catch (Exception ex)
        {
            throw new InvalidDataException(
                "Could not read process or task data from the backup archive. " +
                "The archive may be corrupt.", ex);
        }

        return new SiteRestoreDelta
        {
            HasBackupFiles = manifest.Contents.HasFiles,
            HasBackupDatabases = manifest.Contents.HasDatabases,
            HasBackupProcesses = manifest.Contents.HasProcesses,
            HasBackupScheduledTasks = manifest.Contents.HasScheduledTasks,
            Databases = dbItems,
            Processes = processItems,
            ScheduledTasks = taskItems
        };
    }

    public async Task RestoreToSiteAsync(
        string zipPath,
        SiteModel site,
        SiteRestoreOptions options,
        SiteRollbackDeletions? deletions = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"stackroot-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            progress?.Report("Extracting archive…");
            await Task.Run(() =>
            {
                var zipSize = new FileInfo(zipPath).Length;
                var drive = new DriveInfo(Path.GetPathRoot(tempDir) ?? tempDir);
                if (drive.AvailableFreeSpace < zipSize * 2)
                    throw new IOException(
                        $"Not enough disk space to extract the backup. " +
                        $"Need at least {zipSize * 2 / (1024 * 1024):N0} MB free on {drive.Name}.");
                ZipFile.ExtractToDirectory(zipPath, tempDir, overwriteFiles: true);
            }).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            if (options.RestoreFiles)
            {
                var filesDir = Path.Combine(tempDir, "files");
                if (Directory.Exists(filesDir))
                {
                    progress?.Report("Restoring site files…");
                    await Task.Run(() =>
                    {
                        if (options.SkipFileSafetyCopy)
                        {
                            // Fast path: overwrite directly without a safety copy
                            CopyDirectory(filesDir, site.Path);
                        }
                        else
                        {
                            var rollbackPath = site.Path + ".bak";
                            try
                            {
                                if (Directory.Exists(site.Path))
                                    Directory.Move(site.Path, rollbackPath);
                                CopyDirectory(filesDir, site.Path);
                                if (Directory.Exists(rollbackPath))
                                    DeleteDirectoryForce(rollbackPath);
                            }
                            catch
                            {
                                if (Directory.Exists(rollbackPath) && !Directory.Exists(site.Path))
                                    Directory.Move(rollbackPath, site.Path);
                                throw;
                            }
                        }
                    }).ConfigureAwait(false);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (options.RestoreDatabases)
            {
                var manifest = ReadManifestFromDir(tempDir);
                var existingDbNames = _databaseManager.List()
                    .Select(d => d.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // Build the per-DB selection filter (null = restore all)
                var dbRestoreFilter = options.DatabaseNamesToRestore is not null
                    ? new HashSet<string>(options.DatabaseNamesToRestore, StringComparer.OrdinalIgnoreCase)
                    : null;

                var databasesToRestore = manifest.Databases
                    .Where(db => dbRestoreFilter is null || dbRestoreFilter.Contains(db.Name))
                    .ToList();

                var rollbackDumps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!options.SkipDbSafetyCopy)
                {
                    // Dump each DB that will be replaced so we can roll back if a later DB fails
                    var rollbackDir = Path.Combine(tempDir, "db-rollback");
                    Directory.CreateDirectory(rollbackDir);
                    foreach (var db in databasesToRestore)
                    {
                        if (!existingDbNames.Contains(db.Name)) continue;
                        try
                        {
                            var dumpPath = await Task.Run(() => _databaseManager.Backup(db.Name, rollbackDir))
                                .ConfigureAwait(false);
                            rollbackDumps[db.Name] = dumpPath;
                        }
                        catch { /* non-fatal: proceed without rollback for this DB */ }
                    }
                }

                var restoredDbs = new List<string>();
                try
                {
                    foreach (var db in databasesToRestore)
                    {
                        var sqlFile = Path.Combine(tempDir, db.File.Replace('/', Path.DirectorySeparatorChar));
                        if (!File.Exists(sqlFile)) continue;

                        progress?.Report($"Restoring database: {db.Name}…");
                        await Task.Run(() =>
                        {
                            _databaseManager.RestoreBackup(sqlFile, db.Name, replaceExistingDatabase: true);
                            _databaseManager.LinkToSite(db.Name, site.Id);
                        }).ConfigureAwait(false);
                        restoredDbs.Add(db.Name);
                    }
                }
                catch
                {
                    if (!options.SkipDbSafetyCopy)
                    {
                        foreach (var dbName in restoredDbs)
                        {
                            try
                            {
                                if (rollbackDumps.TryGetValue(dbName, out var dumpPath))
                                {
                                    progress?.Report($"Rolling back database: {dbName}…");
                                    await Task.Run(() =>
                                        _databaseManager.RestoreBackup(dumpPath, dbName, replaceExistingDatabase: true))
                                        .ConfigureAwait(false);
                                }
                                else
                                {
                                    await Task.Run(() => _databaseManager.Delete(dbName, dropFromServer: true))
                                        .ConfigureAwait(false);
                                }
                            }
                            catch { /* best-effort rollback */ }
                        }
                    }
                    throw;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (options.RestoreProcesses)
            {
                progress?.Report("Restoring processes…");
                await Task.Run(() =>
                {
                    var backupIds = ReadBackupProcessIds(tempDir);
                    foreach (var p in _processManager.List(site.Id))
                        if (backupIds.Contains(p.Id))
                            _processManager.Remove(p.Id);
                    RestoreProcesses(tempDir, site.Id, freshIds: false);
                }).ConfigureAwait(false);
            }

            if (options.RestoreScheduledTasks)
            {
                progress?.Report("Restoring scheduled tasks…");
                await Task.Run(() =>
                {
                    var backupIds = ReadBackupTaskIds(tempDir);
                    foreach (var t in _taskScheduler.List().Where(t => string.Equals(t.SiteId, site.Id, StringComparison.OrdinalIgnoreCase)))
                        if (backupIds.Contains(t.Id))
                            _taskScheduler.Delete(t.Id);
                    RestoreScheduledTasks(tempDir, site.Id, freshIds: false);
                }).ConfigureAwait(false);
            }

            var siteDataSrc = Path.Combine(tempDir, "site-data");
            if (Directory.Exists(siteDataSrc))
            {
                progress?.Report("Restoring site data…");
                var siteDataDest = Path.Combine(_paths.SitesRoot, site.Id);
                await Task.Run(() => CopyDirectory(siteDataSrc, siteDataDest)).ConfigureAwait(false);
            }

            // Apply user-selected deletions (items present in current site but not in backup)
            if (deletions is not null)
            {
                foreach (var dbName in deletions.DatabaseNamesToDelete)
                {
                    progress?.Report($"Deleting database: {dbName}…");
                    await Task.Run(() => _databaseManager.Delete(dbName, dropFromServer: true)).ConfigureAwait(false);
                }

                foreach (var processId in deletions.ProcessIdsToDelete)
                    await Task.Run(() => _processManager.Remove(processId)).ConfigureAwait(false);

                foreach (var taskId in deletions.TaskIdsToDelete)
                    await Task.Run(() => _taskScheduler.Delete(taskId)).ConfigureAwait(false);
            }

            progress?.Report("Done.");
        }
        finally
        {
            await Task.Run(() => { try { Directory.Delete(tempDir, recursive: true); } catch { } })
                .ConfigureAwait(false);
        }
    }

    public async Task<SiteModel> ImportSiteAsync(
        string zipPath,
        string? newDomain,
        SiteRestoreOptions options,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"stackroot-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("Extracting archive…");
            await Task.Run(() =>
            {
                var zipSize = new FileInfo(zipPath).Length;
                var drive = new DriveInfo(Path.GetPathRoot(tempDir) ?? tempDir);
                if (drive.AvailableFreeSpace < zipSize * 2)
                    throw new IOException(
                        $"Not enough disk space to extract the backup. " +
                        $"Need at least {zipSize * 2 / (1024 * 1024):N0} MB free on {drive.Name}.");
                ZipFile.ExtractToDirectory(zipPath, tempDir, overwriteFiles: true);
            }, cancellationToken).ConfigureAwait(false);

            var manifest = ReadManifestFromDir(tempDir);
            var domain = !string.IsNullOrWhiteSpace(newDomain) ? newDomain.Trim() : manifest.SiteDomain;

            SiteModel? originalSite = null;
            var siteJsonPath = Path.Combine(tempDir, "site.json");
            if (File.Exists(siteJsonPath))
            {
                var siteJson = await File.ReadAllTextAsync(siteJsonPath).ConfigureAwait(false);
                originalSite = JsonSerializer.Deserialize<SiteModel>(siteJson, JsonOpts);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Creating site {domain}…");

            var input = new CreateSiteInput
            {
                Name = originalSite?.Name ?? manifest.SiteName,
                Domain = domain,
                DomainAliases = originalSite?.DomainAliases,
                Template = originalSite?.Template ?? SiteTemplateIds.Static,
                PhpVersionId = originalSite?.PhpVersionId
            };

            SiteModel newSite = null!;
            await Task.Run(() => newSite = _siteManager.Create(input), cancellationToken).ConfigureAwait(false);

            try
            {
                // Apply additional settings not available in CreateSiteInput
                if (originalSite is not null)
                {
                    var hasPatch = originalSite.DevProxies?.Count > 0
                        || originalSite.ForceHttps == true
                        || (!string.IsNullOrWhiteSpace(originalSite.DocumentRoot) && originalSite.DocumentRoot != ".");
                    if (hasPatch)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        progress?.Report("Applying site settings…");
                        var patch = new UpdateSiteInput
                        {
                            DevProxies = originalSite.DevProxies?.Count > 0 ? originalSite.DevProxies : null,
                            ForceHttps = originalSite.ForceHttps,
                            DocumentRoot = (!string.IsNullOrWhiteSpace(originalSite.DocumentRoot) && originalSite.DocumentRoot != ".")
                                ? originalSite.DocumentRoot
                                : null
                        };
                        await Task.Run(() => newSite = _siteManager.Update(newSite.Id, patch) ?? newSite, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                if (options.RestoreFiles)
                {
                    var filesDir = Path.Combine(tempDir, "files");
                    if (Directory.Exists(filesDir))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        progress?.Report("Restoring site files…");
                        await Task.Run(() => CopyDirectory(filesDir, newSite.Path), cancellationToken).ConfigureAwait(false);
                    }
                }

                if (options.RestoreDatabases)
                {
                    foreach (var db in manifest.Databases)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var sqlFile = Path.Combine(tempDir, db.File.Replace('/', Path.DirectorySeparatorChar));
                        if (!File.Exists(sqlFile)) continue;

                        // Safety check: skip if already linked to another site
                        var existing = _databaseManager.List().FirstOrDefault(d =>
                            string.Equals(d.Name, db.Name, StringComparison.OrdinalIgnoreCase));
                        if (existing?.SiteId is { Length: > 0 })
                            continue;

                        progress?.Report($"Importing database: {db.Name}…");
                        await Task.Run(() =>
                        {
                            _databaseManager.RestoreBackup(sqlFile, db.Name, replaceExistingDatabase: false);
                            _databaseManager.LinkToSite(db.Name, newSite.Id);
                        }, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (options.RestoreProcesses)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report("Importing processes…");
                    await Task.Run(() => RestoreProcesses(tempDir, newSite.Id, freshIds: true), cancellationToken).ConfigureAwait(false);
                }

                if (options.RestoreScheduledTasks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report("Importing scheduled tasks…");
                    await Task.Run(() => RestoreScheduledTasks(tempDir, newSite.Id, freshIds: true), cancellationToken).ConfigureAwait(false);
                }

                var siteDataSrc = Path.Combine(tempDir, "site-data");
                if (Directory.Exists(siteDataSrc))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report("Restoring site data…");
                    var siteDataDest = Path.Combine(_paths.SitesRoot, newSite.Id);
                    await Task.Run(() => CopyDirectory(siteDataSrc, siteDataDest), cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                try { _siteManager.Delete(newSite.Id, deleteDatabases: true); } catch { }
                throw;
            }

            progress?.Report("Done.");
            return newSite;
        }
        finally
        {
            await Task.Run(() => { try { Directory.Delete(tempDir, recursive: true); } catch { } })
                .ConfigureAwait(false);
        }
    }

    private void RestoreProcesses(string tempDir, string siteId, bool freshIds)
    {
        var jsonPath = Path.Combine(tempDir, "processes.json");
        if (!File.Exists(jsonPath)) return;

        var json = File.ReadAllText(jsonPath);
        var node = JsonNode.Parse(json) as JsonObject;
        if (node?["processes"] is not JsonArray processes) return;

        foreach (var item in processes)
        {
            if (item is null) continue;
            var process = JsonSerializer.Deserialize<GlobalProcess>(item.ToJsonString(), JsonSerializerConfig.Default);
            if (process is null) continue;
            var restored = process with
            {
                Id = freshIds ? Guid.NewGuid().ToString("N")[..12] : process.Id,
                SiteId = siteId
            };
            _processManager.Add(restored);
        }
    }

    private void RestoreScheduledTasks(string tempDir, string siteId, bool freshIds)
    {
        var jsonPath = Path.Combine(tempDir, "scheduled-tasks.json");
        if (!File.Exists(jsonPath)) return;

        var json = File.ReadAllText(jsonPath);
        var node = JsonNode.Parse(json) as JsonObject;
        if (node?["tasks"] is not JsonArray tasks) return;

        foreach (var item in tasks)
        {
            if (item is null) continue;
            var taskNode = JsonNode.Parse(item.ToJsonString()) as JsonObject;
            if (taskNode is null) continue;
            if (freshIds) taskNode.Remove("id");
            var task = taskNode.Deserialize<ScheduledTaskModel>(JsonOpts);
            if (task is null) continue;
            task.SiteId = siteId;
            _taskScheduler.Add(task);
        }
    }

    private static List<GlobalProcess> ReadProcessesFromEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var json = Encoding.UTF8.GetString(ms.ToArray());
        var node = JsonNode.Parse(json) as JsonObject;
        if (node?["processes"] is not JsonArray processes) return [];

        var result = new List<GlobalProcess>();
        foreach (var item in processes)
        {
            if (item is null) continue;
            try
            {
                var p = JsonSerializer.Deserialize<GlobalProcess>(item.ToJsonString(), JsonSerializerConfig.Default);
                if (p is not null) result.Add(p);
            }
            catch { }
        }
        return result;
    }

    private static List<ScheduledTaskModel> ReadTasksFromEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var json = Encoding.UTF8.GetString(ms.ToArray());
        var node = JsonNode.Parse(json) as JsonObject;
        if (node?["tasks"] is not JsonArray tasks) return [];

        var result = new List<ScheduledTaskModel>();
        foreach (var item in tasks)
        {
            if (item is null) continue;
            try
            {
                var t = item.Deserialize<ScheduledTaskModel>(JsonOpts);
                if (t is not null) result.Add(t);
            }
            catch { }
        }
        return result;
    }

    private static HashSet<string> ReadBackupProcessIds(string tempDir)
    {
        var jsonPath = Path.Combine(tempDir, "processes.json");
        if (!File.Exists(jsonPath)) return [];
        var node = JsonNode.Parse(File.ReadAllText(jsonPath)) as JsonObject;
        if (node?["processes"] is not JsonArray processes) return [];
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in processes)
        {
            var id = (item as JsonObject)?["id"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(id)) ids.Add(id);
        }
        return ids;
    }

    private static HashSet<string> ReadBackupTaskIds(string tempDir)
    {
        var jsonPath = Path.Combine(tempDir, "scheduled-tasks.json");
        if (!File.Exists(jsonPath)) return [];
        var node = JsonNode.Parse(File.ReadAllText(jsonPath)) as JsonObject;
        if (node?["tasks"] is not JsonArray tasks) return [];
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in tasks)
        {
            var id = (item as JsonObject)?["id"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(id)) ids.Add(id);
        }
        return ids;
    }

    private static BackupManifest ReadManifestFromDir(string dir)
    {
        var path = Path.Combine(dir, "manifest.json");
        var json = File.ReadAllText(path);
        var node = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidDataException("manifest.json is not a valid JSON object.");
        return ParseManifest(node);
    }

    private static BackupManifest ParseManifest(JsonObject node)
    {
        var contentsNode = node["contents"] as JsonObject;
        var databasesNode = node["databases"] as JsonArray;

        var contents = new BackupManifestContents(
            HasFiles: contentsNode?["hasFiles"]?.GetValue<bool>() ?? false,
            HasDatabases: contentsNode?["hasDatabases"]?.GetValue<bool>() ?? false,
            HasProcesses: contentsNode?["hasProcesses"]?.GetValue<bool>() ?? false,
            HasScheduledTasks: contentsNode?["hasScheduledTasks"]?.GetValue<bool>() ?? false);

        var databases = databasesNode?
            .OfType<JsonObject>()
            .Select(db => new BackupManifestDatabase(
                Name: db["name"]?.GetValue<string>() ?? string.Empty,
                Engine: db["engine"]?.GetValue<string>() ?? string.Empty,
                File: db["file"]?.GetValue<string>() ?? string.Empty))
            .ToList() ?? [];

        var schemaVersion = node["schemaVersion"]?.GetValue<string>() ?? "1";
        if (schemaVersion != "1")
            throw new NotSupportedException(
                $"Backup schema version '{schemaVersion}' is not supported. " +
                "Update Stackroot to restore this backup.");

        return new BackupManifest(
            SchemaVersion: schemaVersion,
            BackupDate: node["backupDate"]?.GetValue<string>() ?? string.Empty,
            SiteName: node["siteName"]?.GetValue<string>() ?? string.Empty,
            SiteDomain: node["siteDomain"]?.GetValue<string>() ?? string.Empty,
            SiteId: node["siteId"]?.GetValue<string>() ?? string.Empty,
            Contents: contents,
            Databases: databases);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destFile = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            File.Copy(file, destFile, overwrite: true);
        }
    }

    private static void DeleteDirectoryForce(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attr = File.GetAttributes(file);
            if ((attr & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attr & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(path, recursive: true);
    }
}
