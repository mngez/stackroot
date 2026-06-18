using System.IO;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Node;
using Stackroot.Core.Observability;
using Stackroot.Core.Services;
using Stackroot.Core.Settings;
using Stackroot.Core.Windows;

namespace Stackroot.App.Services;

public sealed class PackageInstallCoordinator
{
    private readonly PackageInstaller _installer;
    private readonly ComposerInstaller _composerInstaller;
    private readonly NpmPrefixPackageInstaller _npmPrefixInstaller;
    private readonly LaravelInstaller _laravelInstaller;
    private readonly InstallRegistryStore _registry;
    private readonly SettingsStore _settings;
    private readonly NodeManager _nodeManager;
    private readonly StackrootBinManager _binManager;
    private readonly PackageCatalogStore _catalog;
    private readonly INpmTooling _npmTooling;
    private readonly StackrootPaths _paths;
    private readonly IDiagnosticsReporter _diagnostics;

    public PackageInstallCoordinator(
        PackageInstaller installer,
        InstallRegistryStore registry,
        PackageCatalogStore catalog,
        SettingsStore settings,
        NodeManager nodeManager,
        StackrootBinManager binManager,
        StackrootPaths paths,
        INpmTooling npmTooling,
        IDiagnosticsReporter diagnostics)
    {
        _installer = installer;
        _registry = registry;
        _catalog = catalog;
        _settings = settings;
        _nodeManager = nodeManager;
        _binManager = binManager;
        _paths = paths;
        _npmTooling = npmTooling;
        _diagnostics = diagnostics;
        _composerInstaller = new ComposerInstaller(paths.DataRoot);
        _npmPrefixInstaller = new NpmPrefixPackageInstaller(paths.DataRoot, npmTooling);
        _laravelInstaller = new LaravelInstaller();
    }

    public PackageInstaller Installer => _installer;

    public bool IsInstalling(string packageId) => _installer.IsInstalling(packageId);

    public void CancelInstall(string packageId) => _installer.CancelInstall(packageId);

    public Task<string> InstallAsync(
        PackageEntry entry,
        InstallProgressCallback? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        return InstallInternalAsync(entry, onProgress, null, cancellationToken);
    }

    public Task<string> InstallAsync(
        PackageEntry entry,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        return InstallInternalAsync(entry, null, progress, cancellationToken);
    }

    public Task UninstallAsync(PackageEntry entry, CancellationToken cancellationToken = default) =>
        _diagnostics.RunUserActionAsync(
            "PackageUninstall",
            entry.Id,
            async () =>
            {
                await PrepareForUninstallAsync(entry, cancellationToken).ConfigureAwait(false);
                await _installer.UninstallAsync(entry, cancellationToken).ConfigureAwait(false);
                await _binManager.SyncStackrootBinAsync(cancellationToken).ConfigureAwait(false);
                _diagnostics.LogActivity("PackageUninstall", $"Removed {entry.Id}");
            },
            cancellationToken);

    private async Task PrepareForUninstallAsync(PackageEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Type != PackageType.Php)
        {
            return;
        }

        _diagnostics.LogActivity("PackageUninstall", $"Stopping PHP FastCGI before removing {entry.Id}");
        await PhpCgiRuntime.StopPhpCgiAsync(entry.Id, cancellationToken).ConfigureAwait(false);
        var settings = _settings.Load();
        PhpCgiRuntime.KillOwnedPhpCgiOnPort(settings, _registry, entry.Id);
        await PortProbe.SleepAsync(500, cancellationToken).ConfigureAwait(false);
    }

    public async Task RepairInstalledToolsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var phpExe = ResolveActivePhpExe();
        foreach (var composer in _registry.List(PackageType.Composer))
        {
            if (phpExe is null)
            {
                continue;
            }

            using (_diagnostics.BeginAction("ToolRepair", $"Composer {composer.Id}"))
            {
                ComposerInstaller.RepairInstallation(composer.InstallPath, phpExe);
            }
        }

        await RepairBrokenNpmPrefixToolsAsync(cancellationToken).ConfigureAwait(false);
        await EnsureDefaultPnpmAsync(cancellationToken).ConfigureAwait(false);
        await _binManager.SyncStackrootBinAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RepairBrokenNpmPrefixToolsAsync(CancellationToken cancellationToken)
    {
        foreach (var package in _registry.List(PackageType.Pnpm).Concat(_registry.List(PackageType.Vite)))
        {
            if (NpmPrefixPackageDefaults.IsInstallHealthy(package))
            {
                continue;
            }

            var entry = _catalog.GetById(package.Id) ?? NpmPrefixPackageDefaults.FallbackEntryFor(package);
            if (entry is null)
            {
                continue;
            }

            try
            {
                var action = package.Type == PackageType.Pnpm ? "Repair pnpm" : "Repair vite";
                await _diagnostics.RunActionAsync(
                    "ToolRepair",
                    $"{action} {package.Id}",
                    async () =>
                    {
                        if (package.Type == PackageType.Pnpm)
                        {
                            await _npmPrefixInstaller.InstallPnpmAsync(entry, _paths.RuntimeRoot, cancellationToken: cancellationToken)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            await _npmPrefixInstaller.InstallViteAsync(entry, _paths.RuntimeRoot, cancellationToken: cancellationToken)
                                .ConfigureAwait(false);
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _diagnostics.LogUserError("ToolRepair", $"{package.Id}: {ex.Message}");
            }
        }
    }

    private async Task EnsureDefaultPnpmAsync(CancellationToken cancellationToken)
    {
        var existing = _registry.GetById(NpmPrefixPackageDefaults.DefaultPnpmPackageId);
        if (existing is not null && NpmPrefixPackageDefaults.IsInstallHealthy(existing))
        {
            return;
        }

        var defaultPath = CatalogPaths.InstallDirForPackage(
            _paths.RuntimeRoot,
            NpmPrefixPackageDefaults.DefaultPnpmEntry().InstallDir);
        var defaultCmd = Path.Combine(defaultPath, "node_modules", ".bin", "pnpm.cmd");
        if (File.Exists(defaultCmd) && existing is null)
        {
            _registry.Register(new InstalledPackage
            {
                Id = NpmPrefixPackageDefaults.DefaultPnpmPackageId,
                Type = PackageType.Pnpm,
                Version = NpmPrefixPackageDefaults.DefaultPnpmVersion,
                InstalledAt = DateTimeOffset.UtcNow.ToString("O"),
                InstallPath = defaultPath,
                Source = PackageSourceType.Remote
            });
            return;
        }

        if (_npmTooling.ResolveNpmCommand() is null)
        {
            return;
        }

        try
        {
            await _diagnostics.RunActionAsync(
                "ToolRepair",
                "Ensure default pnpm",
                () => _npmPrefixInstaller.InstallPnpmAsync(
                    NpmPrefixPackageDefaults.DefaultPnpmEntry(),
                    _paths.RuntimeRoot,
                    cancellationToken: cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _diagnostics.LogUserError("ToolRepair", $"Default pnpm: {ex.Message}");
        }
    }

    private async Task<string> InstallInternalAsync(
        PackageEntry entry,
        InstallProgressCallback? onProgress,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        void ReportProgress(InstallProgress payload)
        {
            onProgress?.Invoke(payload);
            progress?.Report(payload);
            _installer.ReportExternalProgress(payload);
        }

        void ReportPhase(InstallPhase phase, int percent, string message)
        {
            ReportProgress(new InstallProgress
            {
                PackageId = entry.Id,
                Phase = phase,
                Percent = percent,
                Message = message
            });
        }

        string installPath;
        using var scope = _diagnostics.BeginAction("PackageInstall", entry.Id);
        ReportPhase(InstallPhase.Resolving, 0, $"Preparing {entry.Label}…");
        switch (entry.Type)
        {
            case PackageType.Composer:
            {
                var phpExe = ResolveActivePhpExe()
                    ?? throw new InvalidOperationException("Install and activate a PHP version before Composer.");
                installPath = await _composerInstaller.InstallAsync(
                    entry,
                    _paths.RuntimeRoot,
                    phpExe,
                    ReportProgress,
                    cancellationToken);
                break;
            }
            case PackageType.Pnpm:
                installPath = await _npmPrefixInstaller.InstallPnpmAsync(
                    entry,
                    _paths.RuntimeRoot,
                    ReportProgress,
                    cancellationToken);
                break;
            case PackageType.Vite:
                installPath = await _npmPrefixInstaller.InstallViteAsync(
                    entry,
                    _paths.RuntimeRoot,
                    ReportProgress,
                    cancellationToken);
                break;
            case PackageType.Laravel:
            {
                var phpExe = ResolveActivePhpExe()
                    ?? throw new InvalidOperationException("Install and activate a PHP version before the Laravel installer.");
                installPath = await _laravelInstaller.InstallAsync(
                    entry,
                    _paths.RuntimeRoot,
                    _registry,
                    phpExe,
                    _binManager.BinDirectory,
                    ReportProgress,
                    cancellationToken);
                break;
            }
            default:
                installPath = await _installer.InstallAsync(entry, ReportProgress, cancellationToken);
                if (entry.Type == PackageType.Nvm)
                {
                    _nodeManager.ConfigureAfterNvmInstall(installPath);
                }

                break;
        }

        await Task.Run(async () => await _binManager.SyncStackrootBinAsync(cancellationToken).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
        ReportPhase(InstallPhase.Done, 100, "Installation complete.");
        _diagnostics.LogActivity("PackageInstall", $"Finished install: {entry.Id}");
        return installPath;
    }

    private string? ResolveActivePhpExe()
    {
        var settings = _settings.Load();
        var phpId = settings.Php.ActiveVersionId;
        if (string.IsNullOrWhiteSpace(phpId))
        {
            phpId = _registry.List(PackageType.Php)
                .OrderByDescending(package => package.Version, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()
                ?.Id;
        }

        if (string.IsNullOrWhiteSpace(phpId))
        {
            return null;
        }

        var installed = _registry.GetById(phpId);
        return installed is null
            ? null
            : PackageBinaryResolver.ResolvePackageBinary(installed.InstallPath, "php.exe");
    }
}
