using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Databases;
using Stackroot.Core.IO;

namespace Stackroot.App.Services;

public sealed class SiteBackupService
{
    private readonly DatabaseManager _databaseManager;
    private readonly StackrootPaths _paths;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SiteBackupService(DatabaseManager databaseManager, StackrootPaths paths)
    {
        _databaseManager = databaseManager;
        _paths = paths;
    }

    public async Task<string> CreateBackupAsync(
        Core.Sites.Models.Site site,
        string destinationDirectory,
        SiteBackupOptions options,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(destinationDirectory);

        var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd_HHmmss");
        var safeName = SanitizeName(site.Name);
        var zipPath = Path.Combine(destinationDirectory, $"{safeName}_{timestamp}.zip");

        var stagingDir = Path.Combine(Path.GetTempPath(), $"stackroot-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);

        try
        {
            var dbFiles = new List<(string name, string engine, string file)>();

            // databases
            cancellationToken.ThrowIfCancellationRequested();
            if (options.IncludeDatabases)
            {
                var dbDir = Path.Combine(stagingDir, "databases");
                Directory.CreateDirectory(dbDir);

                var linkedDbs = _databaseManager.List()
                    .Where(db => string.Equals(db.SiteId, site.Id, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                await Task.Run(() =>
                {
                    foreach (var db in linkedDbs)
                    {
                        progress?.Report($"Exporting database: {db.Name}…");
                        var exportedPath = _databaseManager.Backup(db.Name, dbDir);
                        dbFiles.Add((db.Name, db.Engine.ToString().ToLowerInvariant(), Path.GetFileName(exportedPath)));
                    }
                }).ConfigureAwait(false);
            }

            // processes — read from JSON and filter by siteId
            cancellationToken.ThrowIfCancellationRequested();
            bool hasActualProcesses = false;
            if (options.IncludeProcesses)
            {
                progress?.Report("Saving processes…");
                var processesJson = FilterJsonRegistryBySiteId(
                    StackrootPathResolver.ProcessesRegistryPath(_paths.DataRoot),
                    "processes",
                    site.Id,
                    out hasActualProcesses);
                if (processesJson is not null)
                    await File.WriteAllTextAsync(Path.Combine(stagingDir, "processes.json"), processesJson).ConfigureAwait(false);
            }

            // scheduled tasks — read from JSON and filter by siteId
            bool hasActualScheduledTasks = false;
            if (options.IncludeScheduledTasks)
            {
                progress?.Report("Saving scheduled tasks…");
                var tasksJson = FilterJsonRegistryBySiteId(
                    StackrootPathResolver.ScheduledTasksPath(_paths.DataRoot),
                    "tasks",
                    site.Id,
                    out hasActualScheduledTasks);
                if (tasksJson is not null)
                    await File.WriteAllTextAsync(Path.Combine(stagingDir, "scheduled-tasks.json"), tasksJson).ConfigureAwait(false);
            }

            // site config
            progress?.Report("Saving site configuration…");
            var siteJson = JsonSerializer.Serialize(site, JsonOpts);
            await File.WriteAllTextAsync(Path.Combine(stagingDir, "site.json"), siteJson).ConfigureAwait(false);

            // site-data (wp-credentials, custom-commands, icons)
            var siteDataDir = Path.Combine(_paths.SitesRoot, site.Id);
            if (Directory.Exists(siteDataDir))
            {
                progress?.Report("Saving site data…");
                var destSiteData = Path.Combine(stagingDir, "site-data");
                await Task.Run(() => CopyDirectory(siteDataDir, destSiteData)).ConfigureAwait(false);
            }

            // compute before manifest so hasFiles is accurate
            var siteFilesSource = (options.IncludeFiles && Directory.Exists(site.Path)) ? site.Path : null;

            // manifest
            var manifest = new
            {
                schemaVersion = "1",
                backupDate = DateTimeOffset.UtcNow.ToString("O"),
                stackrootVersion = GetAppVersion(),
                siteName = site.Name,
                siteDomain = site.Domain,
                siteId = site.Id,
                contents = new
                {
                    hasFiles = siteFilesSource is not null,
                    hasDatabases = options.IncludeDatabases && dbFiles.Count > 0,
                    hasProcesses = options.IncludeProcesses && hasActualProcesses,
                    hasScheduledTasks = options.IncludeScheduledTasks && hasActualScheduledTasks
                },
                databases = dbFiles.Select(db => new { name = db.name, engine = db.engine, file = $"databases/{db.file}" }).ToList()
            };
            var manifestJson = JsonSerializer.Serialize(manifest, JsonOpts);
            await File.WriteAllTextAsync(Path.Combine(stagingDir, "manifest.json"), manifestJson).ConfigureAwait(false);

            // create ZIP — metadata from staging, site files streamed directly from source
            // (avoids copying large site files through Temp, which can trigger AV quarantine)
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("Creating archive…");
            await Task.Run(() =>
            {
                using var fs = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

                // metadata files from staging directory
                foreach (var file in Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(stagingDir, file).Replace('\\', '/');
                    var entry = zip.CreateEntry(relative, CompressionLevel.Optimal);
                    using var entryStream = entry.Open();
                    using var srcStream = File.OpenRead(file);
                    srcStream.CopyTo(entryStream);
                }

                // site files streamed directly from source — no intermediate copy to Temp
                if (siteFilesSource is not null)
                {
                    progress?.Report("Compressing site files…");
                    foreach (var file in Directory.EnumerateFiles(siteFilesSource, "*", SearchOption.AllDirectories))
                    {
                        var relative = "files/" + Path.GetRelativePath(siteFilesSource, file).Replace('\\', '/');
                        var entry = zip.CreateEntry(relative, CompressionLevel.Optimal);
                        using var entryStream = entry.Open();
                        using var srcStream = File.OpenRead(file);
                        srcStream.CopyTo(entryStream);
                    }
                }
            }).ConfigureAwait(false);

            progress?.Report("Done.");
            return zipPath;
        }
        finally
        {
            await Task.Run(() => { try { Directory.Delete(stagingDir, recursive: true); } catch { } }).ConfigureAwait(false);
        }
    }

    private static string? FilterJsonRegistryBySiteId(
        string filePath, string arrayProperty, string siteId, out bool hasItems)
    {
        hasItems = false;
        if (!File.Exists(filePath)) return null;

        try
        {
            var json = File.ReadAllText(filePath);
            var root = JsonNode.Parse(json);
            if (root is not JsonObject obj) return null;

            var filtered = new JsonObject();
            foreach (var prop in obj)
            {
                if (string.Equals(prop.Key, arrayProperty, StringComparison.OrdinalIgnoreCase)
                    && prop.Value is JsonArray arr)
                {
                    var kept = new JsonArray();
                    foreach (var item in arr)
                    {
                        if (item is JsonObject entry &&
                            entry.TryGetPropertyValue("siteId", out var idNode) &&
                            string.Equals(idNode?.GetValue<string>(), siteId, StringComparison.OrdinalIgnoreCase))
                        {
                            kept.Add(JsonNode.Parse(item!.ToJsonString()));
                        }
                    }
                    hasItems = kept.Count > 0;
                    filtered[prop.Key] = kept;
                }
                else
                {
                    filtered[prop.Key] = prop.Value is not null ? JsonNode.Parse(prop.Value.ToJsonString()) : null;
                }
            }

            return filtered.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        catch { hasItems = false; return null; }
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

    private static string SanitizeName(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        var result = new string(chars.ToArray()).Trim('_');
        return string.IsNullOrWhiteSpace(result) ? "site" : result[..Math.Min(result.Length, 40)];
    }

    private static string GetAppVersion()
    {
        try { return System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "unknown"; }
        catch { return "unknown"; }
    }
}

public sealed record SiteBackupOptions(
    bool IncludeFiles,
    bool IncludeDatabases,
    bool IncludeProcesses,
    bool IncludeScheduledTasks,
    bool DeleteSiteAfterBackup);
