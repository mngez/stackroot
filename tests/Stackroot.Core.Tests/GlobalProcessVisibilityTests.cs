using Stackroot.Core.Abstractions;
using Stackroot.Core.Supervisor;
using Stackroot.Core.Windows;
using Xunit;

namespace Stackroot.Core.Tests;

public sealed class GlobalProcessVisibilityTests
{
    [Fact]
    public void List_keeps_processes_that_fail_to_resolve()
    {
        var dataRoot = CreateTempDirectory();
        try
        {
            var manager = CreateManager(dataRoot, new ThrowingResolver("PHP php-8.4.22 is not installed."));
            manager.Add(Process("broken-queue", "Queue worker", autoStart: true));
            manager.Add(Process("chrome", "chrome", autoStart: true));

            var listed = manager.List();

            Assert.Equal(2, listed.Count);
            var broken = Assert.Single(listed, process => process.Id == "broken-queue");
            Assert.False(broken.Available);
            Assert.Equal(ProcessStatus.Error, broken.Status);
            Assert.Contains("php-8.4.22", broken.Message, StringComparison.Ordinal);
            Assert.Contains(listed, process => process.Id == "chrome");
        }
        finally
        {
            TryDeleteDirectory(dataRoot);
        }
    }

    [Fact]
    public async Task AutoStart_continues_after_a_process_fails_to_resolve()
    {
        var dataRoot = CreateTempDirectory();
        try
        {
            var manager = CreateManager(dataRoot, new ThrowingResolver("PHP php-8.4.22 is not installed."));
            manager.Add(Process("broken-queue", "Queue worker", autoStart: true));
            manager.Add(Process("chrome", "chrome", autoStart: true));

            var started = await manager.AutoStartAsync(TimeSpan.Zero);

            Assert.Equal(2, started.Count);
            Assert.All(started, process =>
            {
                Assert.Equal(ProcessStatus.Error, process.Status);
                Assert.Contains("php-8.4.22", process.Message, StringComparison.Ordinal);
            });
        }
        finally
        {
            TryDeleteDirectory(dataRoot);
        }
    }

    private static GlobalProcessManager CreateManager(string dataRoot, IGlobalProcessArgvResolver resolver) =>
        new(new ProcessSupervisor(new NoOpJobManager()), new GlobalProcessStore(dataRoot), resolver);

    private static GlobalProcess Process(string id, string name, bool autoStart) =>
        new()
        {
            Id = id,
            Name = name,
            Runtime = SiteCommandRuntime.Php,
            Argv = ["artisan", "queue:work"],
            Enabled = true,
            AutoStart = autoStart,
            PhpVersionId = "php-8.4.22"
        };

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

    private sealed class ThrowingResolver(string message) : IGlobalProcessArgvResolver
    {
        public IReadOnlyList<string> Resolve(GlobalProcess process) => throw new InvalidOperationException(message);

        public IReadOnlyDictionary<string, string?> BuildEnvironment(GlobalProcess process) =>
            throw new InvalidOperationException(message);

        public string FormatDisplayCommandLine(GlobalProcess process, IReadOnlyList<string> resolvedArgv) =>
            string.Join(' ', process.Argv);

        public string ResolveWorkDir(GlobalProcess process) => process.WorkDir;
    }

    private sealed class NoOpJobManager : IProcessJobManager
    {
        public void AssignProcess(int processId)
        {
        }

        public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
