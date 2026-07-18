using System.Text.Json.Nodes;
using Stackroot.Core.IO;
using Stackroot.Core.IO.Migrations;
using Xunit;

namespace Stackroot.Core.Tests;

public sealed class RuntimeRootMigrationTests
{
    [Fact]
    public void Run_copies_legacy_runtime_and_rebases_installed_paths()
    {
        var root = CreateTempDirectory();
        try
        {
            var dataRoot = Path.Combine(root, "roaming");
            var legacyRuntime = Path.Combine(dataRoot, "runtime");
            var newRuntime = Path.Combine(root, "local", "runtime");
            Directory.CreateDirectory(Path.Combine(legacyRuntime, "php", "8.3.32"));
            File.WriteAllText(Path.Combine(legacyRuntime, "php", "8.3.32", "php.exe"), "php");

            var installedPath = Path.Combine(dataRoot, "installed.json");
            File.WriteAllText(
                installedPath,
                $$"""
                {
                  "schemaVersion": 1,
                  "packages": [
                    {
                      "id": "php-8.3.32",
                      "type": "php",
                      "version": "8.3.32",
                      "installPath": "{{legacyRuntime.Replace("\\", "\\\\")}}\\php\\8.3.32",
                      "installedAt": "2026-01-01T00:00:00Z",
                      "source": "remote"
                    },
                    {
                      "id": "custom-tool",
                      "type": "tool",
                      "version": "1.0.0",
                      "installPath": "D:\\elsewhere\\tool",
                      "installedAt": "2026-01-01T00:00:00Z",
                      "source": "remote"
                    }
                  ]
                }
                """);

            var result = RuntimeRootMigration.Run(new Stackroot.Core.Abstractions.StackrootPaths
            {
                DataRoot = dataRoot,
                RuntimeRoot = newRuntime
            });

            Assert.True(result.Migrated || result.Skipped);
            Assert.True(Directory.Exists(Path.Combine(newRuntime, "php", "8.3.32")));
            Assert.True(File.Exists(Path.Combine(newRuntime, "php", "8.3.32", "php.exe")));
            Assert.True(File.Exists(StackrootPathResolver.RuntimeMigrationMarkerPath(dataRoot)));

            var doc = JsonNode.Parse(File.ReadAllText(installedPath))!.AsObject();
            var packages = doc["packages"]!.AsArray();
            var phpPath = packages[0]!["installPath"]!.GetValue<string>();
            var customPath = packages[1]!["installPath"]!.GetValue<string>();
            Assert.StartsWith(Path.GetFullPath(newRuntime), Path.GetFullPath(phpPath), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(@"D:\elsewhere\tool", customPath);

            // First launch keeps the legacy copy as a rollback safety net.
            Assert.True(Directory.Exists(legacyRuntime));

            // Idempotent second run: app has now started once from the new location, so the
            // legacy Roaming runtime is cleaned up while the new one stays intact.
            var second = RuntimeRootMigration.Run(new Stackroot.Core.Abstractions.StackrootPaths
            {
                DataRoot = dataRoot,
                RuntimeRoot = newRuntime
            });
            Assert.True(second.Skipped);
            Assert.False(Directory.Exists(legacyRuntime));
            Assert.True(File.Exists(Path.Combine(newRuntime, "php", "8.3.32", "php.exe")));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void TryRebasePath_only_rewrites_legacy_prefix()
    {
        var legacy = Path.Combine(Path.GetTempPath(), "stackroot-legacy-runtime");
        var next = Path.Combine(Path.GetTempPath(), "stackroot-local-runtime");
        var prefix = Path.GetFullPath(legacy.TrimEnd('\\')) + Path.DirectorySeparatorChar;

        Assert.True(RuntimeRootMigration.TryRebasePath(
            Path.Combine(legacy, "php", "8.4.23"),
            prefix,
            next,
            out var rebased));
        Assert.Equal(Path.GetFullPath(Path.Combine(next, "php", "8.4.23")), rebased);

        Assert.False(RuntimeRootMigration.TryRebasePath(
            @"C:\custom\php",
            prefix,
            next,
            out _));
    }

    private static string CreateTempDirectory()
        => Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "stackroot-tests", Guid.NewGuid().ToString("N"))).FullName;

    private static void TryDeleteDirectory(string path)
    {
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
