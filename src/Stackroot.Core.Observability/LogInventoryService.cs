using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Observability;

public sealed class LogInventoryService
{
    public LogInventory ScanLogInventory(StackrootPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return ScanLogInventory(paths.LogsRoot);
    }

    public LogInventory ScanLogInventory(string logsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsRoot);

        var files = new List<LogFileEntry>();
        WalkLogs(logsRoot, logsRoot, files);

        var ordered = files
            .OrderByDescending(file => file.SizeBytes)
            .ToList();

        return new LogInventory
        {
            TotalBytes = ordered.Sum(file => file.SizeBytes),
            Files = ordered,
            ServiceFiles = ordered.Where(file => file.Category == LogCategory.Service).ToList(),
            ProcessFiles = ordered.Where(file => file.Category == LogCategory.Process).ToList()
        };
    }

    public LogCleanupResult CleanupLogs(CleanupLogsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var inventory = ScanLogInventory(options.LogsRoot);

        var cutoff = options.OlderThanDays is > 0
            ? DateTimeOffset.UtcNow.AddDays(-options.OlderThanDays.Value)
            : (DateTimeOffset?)null;

        var deleted = 0;
        long freedBytes = 0;

        foreach (var file in inventory.Files)
        {
            var shouldDelete = options.DeleteAll || (cutoff.HasValue && file.ModifiedAt < cutoff.Value);
            if (!shouldDelete)
            {
                continue;
            }

            try
            {
                File.Delete(file.Path);
                deleted++;
                freedBytes += file.SizeBytes;
            }
            catch
            {
                // Ignore locked files and continue cleanup.
            }
        }

        return new LogCleanupResult
        {
            Deleted = deleted,
            FreedBytes = freedBytes
        };
    }

    public LogCleanupResult ApplyLogRetention(StackrootPaths paths, int? retentionDays)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (retentionDays is not > 0)
        {
            return new LogCleanupResult();
        }

        return CleanupLogs(new CleanupLogsOptions
        {
            LogsRoot = paths.LogsRoot,
            OlderThanDays = retentionDays
        });
    }

    private static LogCategory ClassifyLogFile(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').ToLowerInvariant();
        if (normalized.StartsWith("processes/") || normalized.Contains("/processes/"))
        {
            return LogCategory.Process;
        }

        return LogCategory.Service;
    }

    private static void WalkLogs(string directory, string root, List<LogFileEntry> output)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (Directory.Exists(entry))
            {
                WalkLogs(entry, root, output);
                continue;
            }

            if (!File.Exists(entry))
            {
                continue;
            }

            var extension = Path.GetExtension(entry);
            if (!extension.Equals(".log", StringComparison.OrdinalIgnoreCase) &&
                !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var info = new FileInfo(entry);
            var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
            output.Add(new LogFileEntry
            {
                Path = entry,
                RelativePath = relative,
                SizeBytes = info.Length,
                ModifiedAt = info.LastWriteTimeUtc,
                Category = ClassifyLogFile(relative)
            });
        }
    }
}
