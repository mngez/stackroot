using Stackroot.Core.Abstractions;
using Stackroot.Core.Services;
using Stackroot.Core.Settings;
using Xunit;

namespace Stackroot.Core.Tests;

public sealed class ServiceManagerConcurrencyTests
{
    [Fact]
    public async Task ListLiveAsync_supports_concurrent_reads()
    {
        var dataRoot = CreateTempDirectory();
        try
        {
            EnsurePaths(dataRoot);
            var store = new SettingsStore(dataRoot);
            store.Save(SettingsDefaults.CreateDefaultSettings());

            using var manager = new ServiceManager(CreatePaths(dataRoot));
            var tasks = Enumerable.Range(0, 32)
                .Select(_ => manager.ListLiveAsync())
                .ToArray();

            var results = await Task.WhenAll(tasks);

            Assert.All(results, rows => Assert.NotEmpty(rows));
        }
        finally
        {
            TryDeleteDirectory(dataRoot);
        }
    }

    [Fact]
    public void Live_queries_do_not_throw_under_parallel_access()
    {
        var dataRoot = CreateTempDirectory();
        try
        {
            EnsurePaths(dataRoot);
            var store = new SettingsStore(dataRoot);
            store.Save(SettingsDefaults.CreateDefaultSettings());

            using var manager = new ServiceManager(CreatePaths(dataRoot));
            var exceptions = new List<Exception>();

            Parallel.For(
                0,
                64,
                () => new List<Exception>(),
                (_, _, local) =>
                {
                    try
                    {
                        _ = manager.ListLiveAsync().GetAwaiter().GetResult();
                        _ = manager.TryBuildLiveInfo("nginx");
                    }
                    catch (Exception ex)
                    {
                        local.Add(ex);
                    }

                    return local;
                },
                local =>
                {
                    lock (exceptions)
                    {
                        exceptions.AddRange(local);
                    }
                });

            Assert.Empty(exceptions);
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

    private static void EnsurePaths(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(Path.Combine(dataRoot, "runtime"));
        Directory.CreateDirectory(Path.Combine(dataRoot, "resources"));
        Directory.CreateDirectory(Path.Combine(dataRoot, "sites"));
        Directory.CreateDirectory(Path.Combine(dataRoot, "config"));
        Directory.CreateDirectory(Path.Combine(dataRoot, "logs"));
    }

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
