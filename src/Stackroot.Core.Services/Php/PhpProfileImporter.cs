using System.Text.Json;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Abstractions.DataDocuments;
using Stackroot.Core.Catalog;
using Stackroot.Core.IO;
using Stackroot.Core.Settings;

namespace Stackroot.Core.Services.Php;

public sealed class PhpProfileImporter
{
    private readonly PackageCatalogStore _catalogStore;
    private readonly InstallRegistryStore _registryStore;
    private readonly PackageInstaller _packageInstaller;
    private readonly SettingsStore _settingsStore;
    private readonly PhpExtensionManager _extensionManager;
    private readonly PeclInstaller _peclInstaller;
    private readonly PhpExtensionsManifestStore _manifestStore;
    private readonly ServiceManager _serviceManager;

    public PhpProfileImporter(
        PackageCatalogStore catalogStore,
        InstallRegistryStore registryStore,
        PackageInstaller packageInstaller,
        SettingsStore settingsStore,
        PhpExtensionManager extensionManager,
        PeclInstaller peclInstaller,
        PhpExtensionsManifestStore manifestStore,
        ServiceManager serviceManager)
    {
        _catalogStore = catalogStore;
        _registryStore = registryStore;
        _packageInstaller = packageInstaller;
        _settingsStore = settingsStore;
        _extensionManager = extensionManager;
        _peclInstaller = peclInstaller;
        _manifestStore = manifestStore;
        _serviceManager = serviceManager;
    }

    public static PhpProfileDocument Parse(string json) =>
        PhpProfileFileParser.ParseSingle(json);

    public static IReadOnlyList<PhpProfileDocument> ParseProfiles(string json) =>
        PhpProfileFileParser.ParseProfiles(json);

    public async Task<PhpProfileImportResult> ImportAsync(
        PhpProfileDocument profile,
        PhpProfileProgressCallback? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var versionId = profile.TargetVersionId.Trim();
        var result = new PhpProfileImportResult
        {
            TargetVersionId = versionId
        };

        var catalogEntry = _catalogStore.GetById(versionId);
        if (catalogEntry is null)
        {
            throw new InvalidOperationException(
                $"Profile targets '{versionId}', which is not in the package catalog. Export from a machine with that PHP version available.");
        }

        await EnsurePhpInstalledAsync(versionId, catalogEntry, result, onProgress, cancellationToken).ConfigureAwait(false);

        var manifest = _manifestStore.Load();
        var requiredServices = CollectRequiredServices(profile, manifest);
        foreach (var serviceId in requiredServices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureServiceInstalledAsync(serviceId, result, onProgress, cancellationToken).ConfigureAwait(false);
        }

        foreach (var pair in profile.Extensions.Where(static p => p.Value))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureExtensionReadyAsync(versionId, pair.Key, manifest, result, onProgress, cancellationToken)
                .ConfigureAwait(false);
        }

        var versionSettings = ToVersionSettings(profile);
        _extensionManager.ApplyImportedProfile(versionId, versionSettings);

        foreach (var pair in profile.Extensions)
        {
            if (!pair.Value)
            {
                continue;
            }

            var entry = manifest.Extensions.FirstOrDefault(e =>
                string.Equals(e.Id, pair.Key, StringComparison.OrdinalIgnoreCase));
            var installed = _registryStore.GetById(versionId);
            if (installed is null)
            {
                result.Failed.Add($"{pair.Key} (PHP not installed)");
                continue;
            }

            var extDir = Path.Combine(PhpExtensionPolicy.ResolvePackageRoot(installed.InstallPath), "ext");
            var settings = _settingsStore.Load();
            var blockedReason = PhpExtensionPolicy.ExtensionBlockedReason(
                pair.Key,
                entry,
                _registryStore,
                settings,
                extDir);

            if (blockedReason is not null)
            {
                result.Failed.Add($"{pair.Key} ({blockedReason})");
            }
            else
            {
                result.EnabledExtensions.Add(pair.Key);
            }
        }

        onProgress?.Invoke("Restarting PHP FastCGI if needed…");
        await _serviceManager.RestartPhpFastCgiAsync([versionId], cancellationToken).ConfigureAwait(false);

        result.Summary = PhpProfileImportResult.BuildSummary(result);
        result.Succeeded = true;
        return result;
    }

    private static PhpVersionSettings ToVersionSettings(PhpProfileDocument profile) =>
        new()
        {
            MemoryLimit = profile.Runtime.MemoryLimit,
            MaxExecutionTime = profile.Runtime.MaxExecutionTime,
            UploadMaxFilesize = profile.Runtime.UploadMaxFilesize,
            PostMaxSize = profile.Runtime.PostMaxSize,
            DisplayErrors = profile.Runtime.DisplayErrors,
            HideWarnings = profile.Runtime.HideWarnings,
            HideDeprecated = profile.Runtime.HideDeprecated,
            LogErrors = profile.Runtime.LogErrors,
            Extensions = new Dictionary<string, bool>(profile.Extensions, StringComparer.OrdinalIgnoreCase),
            IniOverrides = profile.IniOverrides is null
                ? []
                : new Dictionary<string, string>(profile.IniOverrides, StringComparer.Ordinal)
        };

    private static HashSet<ServiceId> CollectRequiredServices(PhpProfileDocument profile, PhpExtensionsManifest manifest)
    {
        var services = new HashSet<ServiceId>();
        foreach (var pair in profile.Extensions.Where(static p => p.Value))
        {
            var entry = manifest.Extensions.FirstOrDefault(e =>
                string.Equals(e.Id, pair.Key, StringComparison.OrdinalIgnoreCase));
            if (entry?.RequiresAnyService is not { Count: > 0 } required)
            {
                continue;
            }

            foreach (var serviceId in required)
            {
                services.Add(serviceId);
            }
        }

        return services;
    }

    private async Task EnsurePhpInstalledAsync(
        string versionId,
        PackageEntry catalogEntry,
        PhpProfileImportResult result,
        PhpProfileProgressCallback? onProgress,
        CancellationToken cancellationToken)
    {
        if (_registryStore.IsInstalled(versionId))
        {
            return;
        }

        onProgress?.Invoke($"Installing {catalogEntry.Label}…");
        await _packageInstaller.InstallAsync(
            catalogEntry,
            progress => onProgress?.Invoke(progress.Message),
            cancellationToken).ConfigureAwait(false);
        result.InstalledPackages.Add(versionId);
    }

    private async Task EnsureServiceInstalledAsync(
        ServiceId serviceId,
        PhpProfileImportResult result,
        PhpProfileProgressCallback? onProgress,
        CancellationToken cancellationToken)
    {
        var settings = _settingsStore.Load();
        if (PhpExtensionPolicy.IsServiceInstalled(_registryStore, settings, serviceId))
        {
            return;
        }

        var packageId = ResolveServicePackageId(settings, serviceId);
        var package = _catalogStore.GetById(packageId);
        if (package is null)
        {
            result.Failed.Add($"service:{serviceId} (catalog package '{packageId}' not found)");
            return;
        }

        onProgress?.Invoke($"Installing {package.Label}…");
        try
        {
            await _packageInstaller.InstallAsync(
                package,
                progress => onProgress?.Invoke(progress.Message),
                cancellationToken).ConfigureAwait(false);

            var current = settings.Services.TryGetValue(serviceId, out var serviceSettings)
                ? serviceSettings
                : SettingsDefaults.DefaultServices()[serviceId];
            _settingsStore.UpdateService(serviceId, current with
            {
                PackageId = package.Id,
                Enabled = true
            });
            result.InstalledPackages.Add(package.Id);

            var definition = SettingsDefaults.ServiceDefinitions.First(d => d.Id == serviceId);
            await TryStartServiceAfterInstallAsync(serviceId, definition, result, onProgress, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result.Failed.Add($"service:{serviceId} ({ex.Message})");
        }
    }

    private async Task TryStartServiceAfterInstallAsync(
        ServiceId serviceId,
        ServiceDefinition definition,
        PhpProfileImportResult result,
        PhpProfileProgressCallback? onProgress,
        CancellationToken cancellationToken)
    {
        if (definition.Runtime == ServiceRuntime.Library)
        {
            result.StartedServices.Add(definition.Name);
            return;
        }

        onProgress?.Invoke($"Starting {definition.Name}…");
        try
        {
            var info = await _serviceManager.StartAsync(
                serviceId.ToString().ToLowerInvariant(),
                cancellationToken).ConfigureAwait(false);

            if (info.Status is ServiceStatus.Running or ServiceStatus.Starting || info.PortOpen == true)
            {
                result.StartedServices.Add(definition.Name);
                return;
            }

            result.Failed.Add($"start:{serviceId} ({info.Message ?? "failed"})");
        }
        catch (Exception ex)
        {
            result.Failed.Add($"start:{serviceId} ({ex.Message})");
        }
    }

    private async Task EnsureExtensionReadyAsync(
        string versionId,
        string extensionId,
        PhpExtensionsManifest manifest,
        PhpProfileImportResult result,
        PhpProfileProgressCallback? onProgress,
        CancellationToken cancellationToken)
    {
        var entry = manifest.Extensions.FirstOrDefault(e =>
            string.Equals(e.Id, extensionId, StringComparison.OrdinalIgnoreCase));

        if (entry?.WindowsSupported == false)
        {
            result.Skipped.Add($"{extensionId} (not supported on Windows)");
            return;
        }

        var installed = _registryStore.GetById(versionId);
        if (installed is null)
        {
            result.Failed.Add($"{extensionId} (PHP not installed)");
            return;
        }

        var extDir = Path.Combine(PhpExtensionPolicy.ResolvePackageRoot(installed.InstallPath), "ext");
        if (PhpExtensionPolicy.ExtensionDllExists(extDir, extensionId))
        {
            return;
        }

        if (!string.Equals(entry?.Kind, "pecl", StringComparison.OrdinalIgnoreCase))
        {
            result.Failed.Add($"{extensionId} (bundled DLL missing)");
            return;
        }

        if (_manifestStore.ResolvePeclBuild(extensionId, versionId) is null
            && _manifestStore.ResolvePieInstallSpec(extensionId, versionId) is null)
        {
            result.Skipped.Add($"{extensionId} (no Windows install method)");
            return;
        }

        onProgress?.Invoke($"Installing PECL extension {entry?.Label ?? extensionId}…");
        try
        {
            await _peclInstaller.InstallAsync(
                extensionId,
                versionId,
                (message, _) => onProgress?.Invoke(message),
                cancellationToken).ConfigureAwait(false);
            result.InstalledExtensions.Add(extensionId);
        }
        catch (Exception ex)
        {
            result.Failed.Add($"{extensionId} ({ex.Message})");
        }
    }

    private static string ResolveServicePackageId(AppSettings settings, ServiceId serviceId)
    {
        if (settings.Services.TryGetValue(serviceId, out var serviceSettings)
            && !string.IsNullOrWhiteSpace(serviceSettings.PackageId))
        {
            return serviceSettings.PackageId;
        }

        return SettingsDefaults.DefaultServices()[serviceId].PackageId!;
    }
}
