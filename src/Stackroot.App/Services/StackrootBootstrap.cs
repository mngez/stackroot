using Microsoft.Extensions.DependencyInjection;
using System.IO;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.AdminTools;
using Stackroot.Core.Databases;
using Stackroot.Core.IO;
using Stackroot.Core.IO.Migrations;
using Stackroot.Core.IO.Storage;
using Stackroot.Core.Node;
using Stackroot.Core.Nginx;
using Stackroot.Core.Observability;
using Stackroot.Core.Services;
using Stackroot.Core.Services.Php;
using Stackroot.Core.Settings;
using Stackroot.Core.Sites.Commands;
using Stackroot.Core.Sites.Installers;
using Stackroot.Core.Sites.Management;
using Stackroot.Core.Sites.Models;
using Stackroot.Core.Sites.Nginx;
using Stackroot.Core.Sites.Persistence;
using Stackroot.Core.Supervisor;
using Stackroot.Core.Windows;
using Stackroot.App.Scheduling;
using Stackroot.App.ViewModels;
using Stackroot.App.Views;
using Stackroot.App.Views.Pages;

namespace Stackroot.App.Services;

public static class StackrootBootstrap
{
    public static void Register(IServiceCollection services)
    {
        var repoRoot = ResolveRepoRoot();
        var primaryBootstrapResources = Path.Combine(repoRoot, "resources");
        EnsureBootstrapResourcesSeeded(repoRoot, primaryBootstrapResources);

        services.AddSingleton(_ => StackrootPathResolver.Resolve());

        services.AddSingleton<Stackroot.Core.IO.JsonFileStore>();
        services.AddSingleton<IJsonFileStore, Stackroot.Core.IO.Storage.JsonFileStore>();

        services.AddSingleton<AppSettingsStore>();
        services.AddSingleton<SettingsStore>(provider =>
        {
            var paths = provider.GetRequiredService<StackrootPaths>();
            var json = provider.GetRequiredService<Stackroot.Core.IO.JsonFileStore>();
            return new SettingsStore(paths.DataRoot, json);
        });

        services.AddSingleton<InstallRegistryStore>(provider =>
        {
            var paths = provider.GetRequiredService<StackrootPaths>();
            var jsonStore = provider.GetRequiredService<IJsonFileStore>();
            return new InstallRegistryStore(paths.DataRoot, jsonStore);
        });

        services.AddSingleton<PackageCatalogStore>(provider =>
        {
            var paths = provider.GetRequiredService<StackrootPaths>();
            var jsonStore = provider.GetRequiredService<IJsonFileStore>();
            return new PackageCatalogStore(paths.ResourcesRoot, jsonStore);
        });

        services.AddSingleton<DownloadCacheStore>(provider =>
        {
            var paths = provider.GetRequiredService<StackrootPaths>();
            var settingsStore = provider.GetRequiredService<SettingsStore>();
            return new DownloadCacheStore(() =>
            {
                var settings = settingsStore.Load();
                return DownloadCacheStore.ResolveCacheRoot(paths.DataRoot, settings.General.DownloadCachePath);
            });
        });

        services.AddSingleton<PackageInstaller>(provider =>
        {
            var paths = provider.GetRequiredService<StackrootPaths>();
            var downloadCache = provider.GetRequiredService<DownloadCacheStore>();
            return new PackageInstaller(
                new PackageInstallerOptions(
                    paths.ResourcesRoot,
                    paths.RuntimeRoot,
                    paths.DataRoot,
                    SevenZipPath: ResolveSevenZipPath(repoRoot)),
                provider.GetRequiredService<InstallRegistryStore>(),
                downloadCache: downloadCache);
        });

        services.AddSingleton<IProcessJobManager, ProcessJobManager>();
        services.AddSingleton<INpmTooling, NpmTooling>();
        services.AddSingleton<NodeVersionCatalog>();
        services.AddSingleton<NodeManager>();
        services.AddSingleton<StackrootBinManager>();
        services.AddSingleton<PackageInstallCoordinator>();
        services.AddSingleton<GlobalProcessStore>(provider =>
        {
            var paths = provider.GetRequiredService<StackrootPaths>();
            var jsonStore = provider.GetRequiredService<IJsonFileStore>();
            return new GlobalProcessStore(paths.DataRoot, jsonStore);
        });
        services.AddSingleton<ProcessSupervisor>();
        services.AddSingleton<GlobalProcessManager>(provider =>
        {
            var supervisor = provider.GetRequiredService<ProcessSupervisor>();
            var store = provider.GetRequiredService<GlobalProcessStore>();
            var resolver = provider.GetService<IGlobalProcessArgvResolver>();
            return new GlobalProcessManager(supervisor, store, resolver);
        });
        services.AddSingleton<LogInventoryService>();
        services.AddSingleton<AppErrorLogger>();
        services.AddSingleton<DiagnosticsReportLogger>();
        services.AddSingleton<IDiagnosticsReporter>(provider => provider.GetRequiredService<DiagnosticsReportLogger>());
        services.AddSingleton<PerformanceSampler>();

        services.AddSingleton<BackgroundWorkQueue>();
        services.AddSingleton<DeferredStartupCoordinator>();
        services.AddSingleton<ServiceManager>(provider => new ServiceManager(
            provider.GetRequiredService<StackrootPaths>(),
            provider.GetRequiredService<InstallRegistryStore>(),
            provider.GetRequiredService<SettingsStore>(),
            provider.GetRequiredService<IProcessJobManager>(),
            provider.GetRequiredService<IDiagnosticsReporter>(),
            provider.GetRequiredService<PackageCatalogStore>()));
        services.AddSingleton<PhpConfigWriter>();
        services.AddSingleton<PhpExtensionsManifestStore>();
        services.AddSingleton<PhpExtensionManager>();
        services.AddSingleton<PeclInstaller>();
        services.AddSingleton<PhpProfileExporter>();
        services.AddSingleton<PhpProfileImporter>();
        services.AddSingleton<DatabaseRegistryStore>(provider =>
        {
            var paths = provider.GetRequiredService<StackrootPaths>();
            var jsonStore = provider.GetRequiredService<IJsonFileStore>();
            return new DatabaseRegistryStore(paths.DataRoot, jsonStore);
        });
        services.AddSingleton<DatabaseManager>();
        services.AddSingleton<PhpMyAdminManager>();
        services.AddSingleton<AppDomainConfigWriter>();
        services.AddSingleton<PhpRedisAdminManager>();
        services.AddSingleton<MailpitManager>();
        services.AddSingleton<StackrootShutdownCoordinator>();
        services.AddSingleton<NginxWebStackRebuilder>();

        services.AddSingleton<SiteStore>(provider =>
        {
            var paths = provider.GetRequiredService<StackrootPaths>();
            var settingsStore = provider.GetRequiredService<SettingsStore>();
            return new SiteStore(paths.DataRoot, settingsStore);
        });

        services.AddSingleton<SiteNginxVhostWriter>(provider =>
        {
            var paths = provider.GetRequiredService<StackrootPaths>();
            return new SiteNginxVhostWriter(NginxRuntime.SitesEnabledDirectory(paths));
        });

        services.AddSingleton<HostsFileEditor>();
        services.AddSingleton<SiteCommandRunner>();
        services.AddSingleton<ISiteInstaller, LaravelSiteInstaller>();
        services.AddSingleton<ISiteInstaller, WordPressSiteInstaller>();
        services.AddSingleton<WpCliManager>(provider => new WpCliManager(
            provider.GetRequiredService<InstallRegistryStore>(),
            provider.GetRequiredService<PackageCatalogStore>(),
            provider.GetRequiredService<PackageInstaller>(),
            provider.GetRequiredService<IDiagnosticsReporter>()));
        services.AddSingleton<ComposerManager>(provider => new ComposerManager(
            provider.GetRequiredService<InstallRegistryStore>(),
            provider.GetRequiredService<PackageCatalogStore>(),
            provider.GetRequiredService<PackageInstaller>(),
            provider.GetRequiredService<IDiagnosticsReporter>()));
        services.AddSingleton<SiteInstallerRegistry>();
        services.AddSingleton<SiteThumbnailService>(provider =>
            new SiteThumbnailService(
                provider.GetRequiredService<StackrootPaths>(),
                provider.GetRequiredService<IDiagnosticsReporter>()));
        services.AddSingleton<Core.Services.IToastService>(provider =>
            new AppToastService(provider.GetRequiredService<IDiagnosticsReporter>()));
        services.AddSingleton<TaskSchedulerService>();
        services.AddTransient<ScheduledTaskViewModel>();
        services.AddTransient<ScheduledTasksPage>();
        services.AddTransient<CronTaskDialog>();

        services.AddSingleton<SiteManager>(provider => new SiteManager(
            provider.GetRequiredService<SiteStore>(),
            provider.GetRequiredService<SiteNginxVhostWriter>(),
            provider.GetRequiredService<HostsFileEditor>(),
            provider.GetRequiredService<SettingsStore>(),
            provider.GetRequiredService<InstallRegistryStore>(),
            provider.GetRequiredService<SiteCommandRunner>(),
            provider.GetRequiredService<StackrootPaths>(),
            provider.GetRequiredService<DatabaseManager>(),
            provider.GetRequiredService<SiteInstallerRegistry>(),
            provider.GetRequiredService<IDiagnosticsReporter>()));
        services.AddSingleton(_ => SiteTemplates.List());
    }

    public static async Task RunStartupTasksAsync(
        IServiceProvider services,
        IStartupProgressReporter? progress = null,
        CancellationToken cancellationToken = default)
    {
        var diagnostics = services.GetRequiredService<IDiagnosticsReporter>();
        using var startupScope = diagnostics.BeginAction("Startup", "Background startup tasks");

        var paths = services.GetRequiredService<StackrootPaths>();
        var migrationReport = DataMigrationRunner.Run(paths);
        if (migrationReport.HasChanges)
        {
            foreach (var change in migrationReport.Changes)
            {
                diagnostics.LogActivity(
                    "DataMigration",
                    $"{change.DocumentId} v{change.FromVersion}→v{change.ToVersion}: {change.Path}");
            }
        }

        var binManager = services.GetRequiredService<StackrootBinManager>();
        var logInventory = services.GetRequiredService<LogInventoryService>();
        var installer = services.GetRequiredService<PackageInstaller>();
        var settingsStore = services.GetRequiredService<SettingsStore>();
        var settings = settingsStore.Load();
        var registry = services.GetRequiredService<InstallRegistryStore>();
        var databaseManager = services.GetRequiredService<DatabaseManager>();
        var phpConfigWriter = services.GetRequiredService<PhpConfigWriter>();
        var siteManager = services.GetRequiredService<SiteManager>();
        var serviceManager = services.GetRequiredService<ServiceManager>();
        var repoRoot = ResolveRepoRoot();

        await RunStartupStepAsync(
            progress,
            "core",
            "Preparing configuration",
            async () =>
            {
                if (ServiceReconciler.Reconcile(
                        settings,
                        registry,
                        databaseManager.List().Select(db => db.Engine.ToString().ToLowerInvariant()).Distinct().ToArray()))
                {
                    settingsStore.Save(settings);
                    settings = settingsStore.Load();
                    diagnostics.LogActivity("Startup", "Service package settings were updated");
                }

                if (!File.Exists(settingsStore.Path))
                {
                    settingsStore.Save(settings);
                }

                _ = logInventory.ApplyLogRetention(paths, settings.General.LogRetentionDays);

                foreach (var bootstrapSource in ResolveBootstrapResourceSources(repoRoot))
                {
                    await diagnostics.RunActionAsync(
                        "Startup",
                        $"Bootstrap resources from {bootstrapSource}",
                        () => installer.BootstrapResourcesAsync(bootstrapSource, cancellationToken),
                        cancellationToken).ConfigureAwait(false);
                }

                if (CatalogUrlRepair.RepairKnownCatalogUrls(paths.ResourcesRoot))
                {
                    diagnostics.LogActivity("Startup", "Catalog URLs repaired");
                }
            },
            cancellationToken).ConfigureAwait(false);

        var coordinator = services.GetRequiredService<PackageInstallCoordinator>();
        var nodeManager = services.GetRequiredService<NodeManager>();

        await RunStartupStepAsync(
            progress,
            "runtime",
            "Configuring runtimes and tools",
            async () =>
            {
                using (diagnostics.BeginAction("Startup", "Repair Node/nvm configuration"))
                {
                    nodeManager.RepairNvmConfiguration();
                }

                await diagnostics.RunActionAsync(
                    "Startup",
                    "Repair Node runtime symlink",
                    () => nodeManager.RepairNodeRuntimeAsync(cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                await diagnostics.RunActionAsync(
                    "Startup",
                    "Repair installed tools",
                    () => coordinator.RepairInstalledToolsAsync(cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                MailpitInstallPathMigration.Migrate(
                    paths.RuntimeRoot,
                    services.GetRequiredService<InstallRegistryStore>(),
                    services.GetRequiredService<PackageCatalogStore>());
                await diagnostics.RunActionAsync(
                    "Startup",
                    "Sync Stackroot bin shims",
                    () => binManager.SyncStackrootBinAsync(cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        await RunStartupStepAsync(
            progress,
            "stack",
            "Starting web stack",
            async () =>
            {
                ApplyServiceSettings(paths, registry, settings);

                var requiredPhpVersionIds = serviceManager.ResolveRequiredPhpVersionIds();
                _ = phpConfigWriter.WriteRequiredPhpConfigs(settings, requiredPhpVersionIds);

                using (diagnostics.BeginAction("Startup", "Regenerate nginx vhosts for all sites"))
                {
                    NginxWebStackRebuilder.MigrateLegacySiteVhosts(paths);
                    siteManager.RegenerateAll();
                }

                var activityCoordinator = services.GetRequiredService<SessionActivityCoordinator>();
                await activityCoordinator.NotifyCoreStartupFinishedAsync(cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        EnqueueDeferredStartupTasks(services);
    }

    private static void RunStartupStep(
        IStartupProgressReporter? progress,
        string stepId,
        string title,
        Action action)
    {
        progress?.BeginStep(stepId, title);
        try
        {
            action();
            progress?.CompleteStep(stepId);
        }
        catch (Exception ex)
        {
            progress?.FailStep(stepId, ex.Message);
            throw;
        }
    }

    private static async Task RunStartupStepAsync(
        IStartupProgressReporter? progress,
        string stepId,
        string title,
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        progress?.BeginStep(stepId, title);
        try
        {
            await action().ConfigureAwait(false);
            progress?.CompleteStep(stepId);
        }
        catch (Exception ex)
        {
            progress?.FailStep(stepId, ex.Message);
            throw;
        }
    }

    public static void EnqueueDeferredStartupTasks(IServiceProvider services)
    {
        var queue = services.GetRequiredService<BackgroundWorkQueue>();
        var coordinator = services.GetRequiredService<DeferredStartupCoordinator>();
        var startupCancellation = coordinator.CancellationToken;
        services.GetRequiredService<SessionActivityCoordinator>().RegisterDeferredStartupPhases(3);

        queue.Enqueue(
            "Startup",
            "Auto-start enabled services",
            async cancellationToken =>
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, startupCancellation);
                var startupToken = linkedCts.Token;
                try
                {
                    var serviceManager = services.GetRequiredService<ServiceManager>();
                    await serviceManager.AutoStartEnabledServicesAsync(startupToken).ConfigureAwait(false);
                }
                finally
                {
                    services.GetRequiredService<SessionActivityCoordinator>().NotifyDeferredPhaseComplete();
                }
            });

        queue.Enqueue(
            "Startup",
            "Ensure Mailpit auto-start",
            async cancellationToken =>
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, startupCancellation);
                var startupToken = linkedCts.Token;
                try
                {
                    var serviceManager = services.GetRequiredService<ServiceManager>();
                    var mailpitManager = services.GetRequiredService<MailpitManager>();
                    var activityCoordinator = services.GetRequiredService<SessionActivityCoordinator>();
                    var diagnostics = services.GetRequiredService<IDiagnosticsReporter>();
                    var settingsStore = services.GetRequiredService<SettingsStore>();

                    using (activityCoordinator.Suppress("mailpit"))
                    {
                        var mailpitProgressId = activityCoordinator.BeginMailpitStartup();
                        var mailpitStatus = await mailpitManager.EnsureAutoStartAsync(startupToken).ConfigureAwait(false);
                        activityCoordinator.CompleteMailpitStartup(mailpitProgressId, mailpitStatus);
                    }
                }
                finally
                {
                    services.GetRequiredService<SessionActivityCoordinator>().NotifyDeferredPhaseComplete();
                }
            });

        queue.Enqueue(
            "Startup",
            "Write remaining PHP configs",
            cancellationToken =>
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, startupCancellation);
                var startupToken = linkedCts.Token;
                startupToken.ThrowIfCancellationRequested();
                var settingsStore = services.GetRequiredService<SettingsStore>();
                var phpConfigWriter = services.GetRequiredService<PhpConfigWriter>();
                var serviceManager = services.GetRequiredService<ServiceManager>();
                var currentSettings = settingsStore.Load();
                var requiredIds = serviceManager.ResolveRequiredPhpVersionIds();
                _ = phpConfigWriter.WriteRemainingPhpConfigs(currentSettings, requiredIds);
                return Task.CompletedTask;
            });

        queue.Enqueue(
            "Startup",
            "Deferred initialization",
            async cancellationToken =>
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, startupCancellation);
                var startupToken = linkedCts.Token;
                try
                {
                    var diagnostics = services.GetRequiredService<IDiagnosticsReporter>();
                    var serviceManager = services.GetRequiredService<ServiceManager>();
                    var phpMyAdminManager = services.GetRequiredService<PhpMyAdminManager>();
                    var phpRedisAdminManager = services.GetRequiredService<PhpRedisAdminManager>();
                    var webStackRebuilder = services.GetRequiredService<NginxWebStackRebuilder>();
                    var globalProcessManager = services.GetRequiredService<GlobalProcessManager>();
                    var activityCoordinator = services.GetRequiredService<SessionActivityCoordinator>();

                    await serviceManager.WaitForEnabledDatabasePortsAsync(startupToken).ConfigureAwait(false);

                    await ApplyAdminToolSafelyAsync(
                        "phpMyAdmin",
                        () => phpMyAdminManager.ApplyAsync(startupToken),
                        diagnostics).ConfigureAwait(false);
                    await ApplyAdminToolSafelyAsync(
                        "phpRedisAdmin",
                        () => phpRedisAdminManager.ApplyAsync(startupToken),
                        diagnostics).ConfigureAwait(false);

                    var phpProgressId = activityCoordinator.BeginPhpStackStartup();
                    try
                    {
                        await webStackRebuilder.FinalizeAndReloadAsync(startupToken).ConfigureAwait(false);
                        await activityCoordinator.CompletePhpStackStartupAsync(phpProgressId, startupToken).ConfigureAwait(false);
                        diagnostics.LogActivity("Startup", "Nginx reloaded with admin tool routes and php-cgi");
                    }
                    catch (OperationCanceledException) when (startupToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        activityCoordinator.FailPhpStackStartup(phpProgressId, ex.Message);
                        diagnostics.LogUserError("Startup", $"Web stack finalize failed: {ex.Message}");
                    }

                    using (diagnostics.BeginAction("Startup", "Auto-start enabled processes"))
                    {
                        var autoStarted = await globalProcessManager.AutoStartAsync(cancellationToken: startupToken)
                            .ConfigureAwait(false);
                        activityCoordinator.NotifyProcessAutoStart(autoStarted);
                    }

                    diagnostics.LogActivity("Startup", "Deferred initialization completed");
                }
                finally
                {
                    services.GetRequiredService<SessionActivityCoordinator>().NotifyDeferredPhaseComplete();
                }
            },
            onComplete: coordinator.RaiseCompleted);
    }

    private static void ApplyServiceSettings(StackrootPaths paths, InstallRegistryStore registry, AppSettings settings)
    {
        var defaultServices = SettingsDefaults.DefaultServices();
        var nginxSettings = settings.Services.TryGetValue(ServiceId.Nginx, out var configured)
            ? configured
            : defaultServices[ServiceId.Nginx];

        var nginxPackageId = string.IsNullOrWhiteSpace(nginxSettings.PackageId)
            ? defaultServices[ServiceId.Nginx].PackageId
            : nginxSettings.PackageId;
        if (string.IsNullOrWhiteSpace(nginxPackageId))
        {
            return;
        }

        var installedNginx = registry.GetById(nginxPackageId);
        if (installedNginx is null)
        {
            return;
        }

        NginxRuntime.setupNginxRuntime(paths, installedNginx.InstallPath);
        NginxRuntime.writeNginxConfig(paths, nginxSettings);
    }

    private static void EnsureBootstrapResourcesSeeded(string repoRoot, string resourcesRoot)
    {
        var destinationPackages = Path.Combine(resourcesRoot, "packages");
        Directory.CreateDirectory(destinationPackages);

        var sourceDirectories =
            new[]
            {
                Path.Combine(repoRoot, "resources", "packages"),
                Path.Combine(AppContext.BaseDirectory, "resources", "packages")
            };

        foreach (var name in new[] { "catalog.json", "php-extensions.json", "pie.phar" })
        {
            var destination = Path.Combine(destinationPackages, name);
            if (!ShouldSeedResource(destination, name))
            {
                continue;
            }

            var copied = false;
            foreach (var sourceDirectory in sourceDirectories)
            {
                var source = Path.Combine(sourceDirectory, name);
                if (!File.Exists(source))
                {
                    continue;
                }

                if (PathsEqual(source, destination))
                {
                    continue;
                }

                TryCopyResourceWithRetry(source, destination);
                copied = true;
                break;
            }

            if (!copied && !File.Exists(destination))
            {
                if (string.Equals(name, "pie.phar", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                throw new IOException(
                    $"Bootstrap resource '{name}' is missing and could not be copied into:{Environment.NewLine}{destination}");
            }
        }
    }

    private static void TryCopyResourceWithRetry(string source, string destination)
    {
        IOException? lastError = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                File.Copy(source, destination, overwrite: true);
                return;
            }
            catch (IOException ex) when (attempt < 4)
            {
                lastError = ex;
                Thread.Sleep(80 * (attempt + 1));
            }
            catch (IOException ex)
            {
                lastError = ex;
                break;
            }
        }

        throw FileLockInspector.CreateAccessException(
            destination,
            $"copy bootstrap resource from{Environment.NewLine}{source}",
            lastError);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);

    private static bool ShouldSeedResource(string destination, string fileName)
    {
        if (!File.Exists(destination))
        {
            return true;
        }

        if (!string.Equals(fileName, "catalog.json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(
                destination,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            var catalog = System.Text.Json.JsonSerializer.Deserialize<PackageCatalog>(
                json,
                Stackroot.Core.IO.JsonSerializerConfig.Default);
            return catalog?.Packages is null || catalog.Packages.Count == 0;
        }
        catch
        {
            return true;
        }
    }

    private static IEnumerable<string> ResolveBootstrapResourceSources(string repoRoot)
    {
        var primary = Path.Combine(repoRoot, "resources");
        if (Directory.Exists(Path.Combine(primary, "packages")))
        {
            yield return primary;
        }
    }

    private static string? ResolveSevenZipPath(string repoRoot)
    {
        var envPath = Environment.GetEnvironmentVariable("STACKROOT_7Z");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            return envPath;
        }

        var resourcesBundled = Path.Combine(repoRoot, "resources", "tools", "7zip", "7za.exe");
        return File.Exists(resourcesBundled) ? resourcesBundled : null;
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Stackroot.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static async Task ApplyAdminToolSafelyAsync(
        string name,
        Func<Task> apply,
        IDiagnosticsReporter diagnostics)
    {
        using var scope = diagnostics.BeginAction("Startup", $"Apply {name}");
        try
        {
            await apply().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            diagnostics.LogUserError("Startup", $"{name} apply skipped: {ex.Message}");
            diagnostics.LogException("Startup", ex);
        }
    }
}
