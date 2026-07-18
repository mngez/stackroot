using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Services;
using Stackroot.Core.Settings;
using Xunit;

namespace Stackroot.Core.Tests;

/// <summary>
/// Regression coverage for a bug where stopping a "required" PHP-CGI listener from the
/// dashboard was immediately undone: the recovery loop saw the listener go from
/// Running -> Stopped and, because the site config still "required" it, respawned it
/// within seconds — the user's explicit Stop had no lasting effect.
/// </summary>
public sealed class ServiceManagerPhpRecoveryTests
{
    private const string VersionId = "test-php";

    [Fact]
    public async Task StopPhpCgiAsync_blocks_auto_recovery_until_explicit_restart()
    {
        var dataRoot = CreateTempDirectory();
        try
        {
            var paths = EnsurePaths(dataRoot);
            var settingsStore = new SettingsStore(dataRoot);
            var settings = SettingsDefaults.CreateDefaultSettings();
            settings.Php.ActiveVersionId = VersionId;
            settingsStore.Save(settings);

            var registry = new InstallRegistryStore(dataRoot);
            registry.Register(new InstalledPackage
            {
                Id = VersionId,
                Type = PackageType.Php,
                Version = "8.4.23",
                InstallPath = Path.Combine(dataRoot, "php"),
                Source = PackageSourceType.Bundled
            });

            var diagnostics = new RecordingDiagnosticsReporter();
            using var manager = new ServiceManager(paths, registry, settingsStore, diagnostics: diagnostics);

            // User clicks Stop on the PHP listener.
            await manager.StopPhpCgiAsync(VersionId);
            Assert.True(manager.IsPhpVersionUserStopped(VersionId));

            // The background/urgent recovery pass (what the dashboard triggers on every
            // "required listener stopped" snapshot) must not attempt to bring it back.
            diagnostics.Activities.Clear();
            await manager.TryRecoverRequiredPhpAsync(urgent: true);
            Assert.DoesNotContain(diagnostics.Activities, a => a.Contains("Recovering php-cgi", StringComparison.Ordinal));

            // An explicit Restart is the only thing that should re-arm recovery.
            await manager.RestartPhpFastCgiAsync([VersionId]);
            Assert.False(manager.IsPhpVersionUserStopped(VersionId));
        }
        finally
        {
            TryDeleteDirectory(dataRoot);
        }
    }

    [Fact]
    public async Task TryRecoverRequiredPhpAsync_still_attempts_recovery_when_not_user_stopped()
    {
        var dataRoot = CreateTempDirectory();
        try
        {
            var paths = EnsurePaths(dataRoot);
            var settingsStore = new SettingsStore(dataRoot);
            var settings = SettingsDefaults.CreateDefaultSettings();
            settings.Php.ActiveVersionId = VersionId;
            settingsStore.Save(settings);

            var registry = new InstallRegistryStore(dataRoot);
            registry.Register(new InstalledPackage
            {
                Id = VersionId,
                Type = PackageType.Php,
                Version = "8.4.23",
                InstallPath = Path.Combine(dataRoot, "php"),
                Source = PackageSourceType.Bundled
            });

            var diagnostics = new RecordingDiagnosticsReporter();
            using var manager = new ServiceManager(paths, registry, settingsStore, diagnostics: diagnostics);

            // Nobody stopped this version — an unexpected-crash recovery pass must still fire,
            // proving the new user-stopped guard doesn't suppress the legitimate keep-alive path.
            await manager.TryRecoverRequiredPhpAsync(urgent: true);
            Assert.Contains(diagnostics.Activities, a => a.Contains("Recovering php-cgi", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(dataRoot);
        }
    }

    private static string CreateTempDirectory()
        => Path.Combine(Path.GetTempPath(), "stackroot-tests", Guid.NewGuid().ToString("N"));

    private static StackrootPaths EnsurePaths(string dataRoot)
    {
        var paths = new StackrootPaths
        {
            DataRoot = dataRoot,
            RuntimeRoot = Path.Combine(dataRoot, "runtime"),
            ResourcesRoot = Path.Combine(dataRoot, "resources"),
            SitesRoot = Path.Combine(dataRoot, "sites"),
            ConfigRoot = Path.Combine(dataRoot, "config"),
            LogsRoot = Path.Combine(dataRoot, "logs")
        };

        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(paths.RuntimeRoot);
        Directory.CreateDirectory(paths.ResourcesRoot);
        Directory.CreateDirectory(paths.SitesRoot);
        Directory.CreateDirectory(paths.ConfigRoot);
        Directory.CreateDirectory(paths.LogsRoot);
        Directory.CreateDirectory(Path.Combine(dataRoot, "php"));

        return paths;
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

    private sealed class RecordingDiagnosticsReporter : IDiagnosticsReporter
    {
        public List<string> Activities { get; } = [];

        public bool IsEnabled => true;

        public void LogActivity(string area, string message) => Activities.Add($"[{area}] {message}");

        public void LogUserError(string area, string message)
        {
        }

        public void LogException(string area, Exception exception)
        {
        }

        public IDisposable BeginAction(string area, string action) => NoOpScope.Instance;

        private sealed class NoOpScope : IDisposable
        {
            public static readonly NoOpScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
