using Stackroot.Core.Settings;
using Stackroot.Core.Abstractions.DataDocuments;
using Xunit;

namespace Stackroot.Core.Tests;

public sealed class SettingsStoreTryLoadTests
{
    [Fact]
    public void TryLoad_returns_defaults_when_file_is_corrupted()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "stackroot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);

        try
        {
            var store = new SettingsStore(dataRoot);
            File.WriteAllText(store.Path, "{ not valid json");
            var loaded = store.TryLoad(out var settings, out var issue);

            Assert.False(loaded);
            Assert.Equal(SettingsLoadIssue.Corrupted, issue);
            Assert.NotNull(settings);
            Assert.Equal(DataDocumentSchemas.Settings, settings!.SchemaVersion);
        }
        finally
        {
            try
            {
                Directory.Delete(dataRoot, recursive: true);
            }
            catch
            {
                // Best effort cleanup.
            }
        }
    }
}
