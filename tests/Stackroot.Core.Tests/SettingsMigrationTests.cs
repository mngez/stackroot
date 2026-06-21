using System.Text.Json;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Abstractions.DataDocuments;
using Stackroot.Core.IO;
using Stackroot.Core.IO.Migrations;
using Stackroot.Core.Settings;
using Xunit;

namespace Stackroot.Core.Tests;

public sealed class SettingsMigrationTests
{
    [Fact]
    public void DataMigrationRunner_upgrades_legacy_settings_schema()
    {
        var dataRoot = CreateTempDirectory();
        try
        {
            var settingsPath = StackrootPathResolver.SettingsPath(dataRoot);
            Directory.CreateDirectory(dataRoot);
            File.WriteAllText(
                settingsPath,
                """
                {
                  "schemaVersion": 1,
                  "general": { "wwwPath": "C:\\www" },
                  "php": {},
                  "services": {
                    "nginx": { "enabled": true, "port": 80, "host": "127.0.0.1" }
                  }
                }
                """);

            var paths = CreatePaths(dataRoot);
            var report = DataMigrationRunner.Run(paths, allowRepeat: true);

            Assert.True(File.Exists(settingsPath));
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            Assert.Equal(
                DataDocumentSchemas.Settings,
                document.RootElement.GetProperty("schemaVersion").GetInt32());

            var store = new SettingsStore(dataRoot);
            var settings = store.Load();
            Assert.Equal(DataDocumentSchemas.Settings, settings.SchemaVersion);
            Assert.Equal("C:\\www", settings.General.WwwPath);
            Assert.True(report.HasChanges);

            store.UpdateGeneral(new GeneralSettings { AppDomain = "example.test" });
            using var savedDocument = JsonDocument.Parse(File.ReadAllText(settingsPath));
            Assert.Equal(
                DataDocumentSchemas.Settings,
                savedDocument.RootElement.GetProperty("schemaVersion").GetInt32());
        }
        finally
        {
            TryDeleteDirectory(dataRoot);
        }
    }

    private static string CreateTempDirectory()
        => Path.Combine(Path.GetTempPath(), "stackroot-tests", Guid.NewGuid().ToString("N"));

    private static StackrootPaths CreatePaths(string dataRoot) => new()
    {
        DataRoot = dataRoot,
        RuntimeRoot = Path.Combine(dataRoot, "runtime"),
        ResourcesRoot = Path.Combine(dataRoot, "resources"),
        SitesRoot = Path.Combine(dataRoot, "sites"),
        ConfigRoot = Path.Combine(dataRoot, "config"),
        LogsRoot = Path.Combine(dataRoot, "logs")
    };

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort cleanup.
        }
    }
}
