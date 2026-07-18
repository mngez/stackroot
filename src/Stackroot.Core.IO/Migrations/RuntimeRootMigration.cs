using System.Text.Json;
using System.Text.Json.Nodes;
using Stackroot.Core.Abstractions;

namespace Stackroot.Core.IO.Migrations;

/// <summary>
/// Moves runtime packages/shims from %APPDATA%\Stackroot\runtime to
/// %LOCALAPPDATA%\Stackroot\runtime and rebases installed.json paths.
/// Idempotent and safe to retry after partial failure.
/// </summary>
public static class RuntimeRootMigration
{
    private static readonly string MutexName =
        @"Local\Stackroot.RuntimeRootMigration." + Environment.UserName;

    public static RuntimeRootMigrationResult Run(StackrootPaths? overrides = null)
    {
        var dataRoot = string.IsNullOrWhiteSpace(overrides?.DataRoot)
            ? StackrootPathResolver.DefaultDataRoot
            : overrides.DataRoot!;
        var newRuntimeRoot = string.IsNullOrWhiteSpace(overrides?.RuntimeRoot)
            ? StackrootPathResolver.DefaultRuntimeRoot
            : overrides.RuntimeRoot!;
        var legacyRuntimeRoot = Path.Combine(dataRoot, "runtime");

        // Custom/test overrides that already point elsewhere — nothing to migrate.
        if (PathsEqual(legacyRuntimeRoot, newRuntimeRoot))
        {
            return new RuntimeRootMigrationResult(Skipped: true, Reason: "runtime roots identical");
        }

        using var mutex = new Mutex(initiallyOwned: false, MutexName, out _);
        var owned = false;
        try
        {
            owned = mutex.WaitOne(TimeSpan.FromMinutes(5));
            if (!owned)
            {
                return new RuntimeRootMigrationResult(Skipped: true, Reason: "migration lock timeout");
            }

            return RunCore(dataRoot, legacyRuntimeRoot, newRuntimeRoot);
        }
        finally
        {
            if (owned)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static RuntimeRootMigrationResult RunCore(
        string dataRoot,
        string legacyRuntimeRoot,
        string newRuntimeRoot)
    {
        Directory.CreateDirectory(dataRoot);
        var markerPath = StackrootPathResolver.RuntimeMigrationMarkerPath(dataRoot);

        if (File.Exists(markerPath) && Directory.Exists(newRuntimeRoot))
        {
            RebaseInstalledRegistry(dataRoot, legacyRuntimeRoot, newRuntimeRoot);

            // A prior launch already migrated and the app has now started successfully at least
            // once from the new location, so the Roaming copy is safe to remove (best-effort).
            var cleaned = TryDeleteLegacyRuntime(legacyRuntimeRoot, newRuntimeRoot);
            return new RuntimeRootMigrationResult(
                Skipped: true,
                Reason: cleaned ? "already migrated; legacy runtime removed" : "already migrated");
        }

        if (!Directory.Exists(legacyRuntimeRoot))
        {
            Directory.CreateDirectory(newRuntimeRoot);
            WriteMarker(markerPath, legacyRuntimeRoot, newRuntimeRoot, copied: false);
            return new RuntimeRootMigrationResult(Skipped: true, Reason: "no legacy runtime");
        }

        // Destination already populated (previous partial success after rename).
        if (Directory.Exists(newRuntimeRoot) && !IsDirectoryEmpty(newRuntimeRoot))
        {
            RebaseInstalledRegistry(dataRoot, legacyRuntimeRoot, newRuntimeRoot);
            WriteMarker(markerPath, legacyRuntimeRoot, newRuntimeRoot, copied: true);
            return new RuntimeRootMigrationResult(Migrated: true, Reason: "destination already present");
        }

        var stagingRoot = Path.Combine(
            Path.GetDirectoryName(newRuntimeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stackroot"),
            $"runtime.migrating-{Guid.NewGuid():N}");

        try
        {
            CopyDirectory(legacyRuntimeRoot, stagingRoot);

            if (Directory.Exists(newRuntimeRoot))
            {
                var incomplete = newRuntimeRoot + $".incomplete-{Guid.NewGuid():N}";
                Directory.Move(newRuntimeRoot, incomplete);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(newRuntimeRoot)!);
            Directory.Move(stagingRoot, newRuntimeRoot);
            stagingRoot = string.Empty;

            RebaseInstalledRegistry(dataRoot, legacyRuntimeRoot, newRuntimeRoot);
            WriteMarker(markerPath, legacyRuntimeRoot, newRuntimeRoot, copied: true);
            return new RuntimeRootMigrationResult(Migrated: true, Reason: "copied legacy runtime");
        }
        catch
        {
            TryDeleteDirectory(stagingRoot);
            throw;
        }
    }

    /// <summary>
    /// Removes the legacy Roaming runtime once the Local runtime is confirmed populated.
    /// Best-effort: locked files (e.g. a running service) simply leave it for a later launch.
    /// </summary>
    public static bool TryDeleteLegacyRuntime(string legacyRuntimeRoot, string newRuntimeRoot)
    {
        if (PathsEqual(legacyRuntimeRoot, newRuntimeRoot)
            || !Directory.Exists(legacyRuntimeRoot)
            || !Directory.Exists(newRuntimeRoot)
            || IsDirectoryEmpty(newRuntimeRoot))
        {
            return false;
        }

        try
        {
            Directory.Delete(legacyRuntimeRoot, recursive: true);
            return true;
        }
        catch
        {
            // Something under the old runtime is still locked; retry on a future launch.
            return false;
        }
    }

    public static bool RebaseInstalledRegistry(string dataRoot, string legacyRuntimeRoot, string newRuntimeRoot)
    {
        var registryPath = StackrootPathResolver.RegistryPath(dataRoot);
        if (!File.Exists(registryPath))
        {
            return false;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(registryPath));
        }
        catch
        {
            return false;
        }

        if (root is not JsonObject obj || obj["packages"] is not JsonArray packages)
        {
            return false;
        }

        var legacyPrefix = NormalizeDirectoryPrefix(legacyRuntimeRoot);
        var changed = false;
        foreach (var node in packages)
        {
            if (node is not JsonObject package)
            {
                continue;
            }

            var installPath = package["installPath"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(installPath))
            {
                continue;
            }

            if (!TryRebasePath(installPath, legacyPrefix, newRuntimeRoot, out var rebased))
            {
                continue;
            }

            package["installPath"] = rebased;
            changed = true;
        }

        if (!changed)
        {
            return false;
        }

        var json = root.ToJsonString(JsonSerializerConfig.Default);
        var tempPath = $"{registryPath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, registryPath, overwrite: true);
        return true;
    }

    public static bool TryRebasePath(string path, string legacyPrefix, string newRuntimeRoot, out string rebased)
    {
        rebased = path;
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(legacyPrefix))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch
        {
            return false;
        }

        if (!full.StartsWith(legacyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffix = full[legacyPrefix.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        rebased = string.IsNullOrEmpty(suffix)
            ? Path.GetFullPath(newRuntimeRoot)
            : Path.GetFullPath(Path.Combine(newRuntimeRoot, suffix));
        return !string.Equals(full, rebased, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDirectoryPrefix(string directory)
    {
        var full = Path.GetFullPath(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return full + Path.DirectorySeparatorChar;
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, dir);
            Directory.CreateDirectory(Path.Combine(destinationDir, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            // Skip following nvm "current" junction content duplication issues by copying the link target files
            // as normal files when Windows expands them during enumeration.
            var relative = Path.GetRelativePath(sourceDir, file);
            var destination = Path.Combine(destinationDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static void WriteMarker(string markerPath, string legacyRoot, string newRoot, bool copied)
    {
        var payload = new
        {
            completedAt = DateTimeOffset.UtcNow.ToString("O"),
            legacyRuntimeRoot = legacyRoot,
            newRuntimeRoot = newRoot,
            copied
        };
        File.WriteAllText(markerPath, JsonSerializer.Serialize(payload, JsonSerializerConfig.Default));
    }

    private static bool IsDirectoryEmpty(string path)
        => !Directory.EnumerateFileSystemEntries(path).Any();

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            Path.GetFullPath(right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best effort
        }
    }
}

public sealed record RuntimeRootMigrationResult(
    bool Migrated = false,
    bool Skipped = false,
    string Reason = "");
