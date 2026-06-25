using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Settings;

namespace Stackroot.Core.Services.Php;

public sealed record PhpExtensionState(
    string Name,
    string Label,
    string? Description,
    bool Enabled,
    bool Effective,
    bool Available,
    string Kind,
    bool ServiceReady,
    string? BlockedReason,
    bool CanToggle);

public sealed record InstallablePeclExtension(
    string Id,
    string Label,
    string? Description);

public sealed class PhpExtensionManager
{
    private readonly StackrootPaths _paths;
    private readonly SettingsStore _settingsStore;
    private readonly InstallRegistryStore _registry;
    private readonly PhpExtensionsManifestStore _manifestStore;
    private readonly PhpConfigWriter _configWriter;

    public PhpExtensionManager(
        StackrootPaths paths,
        SettingsStore settingsStore,
        InstallRegistryStore registry,
        PhpExtensionsManifestStore manifestStore,
        PhpConfigWriter configWriter)
    {
        _paths = paths;
        _settingsStore = settingsStore;
        _registry = registry;
        _manifestStore = manifestStore;
        _configWriter = configWriter;
    }

    public PhpExtensionsManifest GetManifest() => _manifestStore.Load();

    public PhpVersionSettings ResolveVersionSettings(string versionId)
    {
        var settings = _settingsStore.Load();
        return ResolveVersionSettings(settings, versionId);
    }

    public PhpVersionSettings EnsureVersionSettings(string versionId)
    {
        var settings = _settingsStore.Load();
        var versions = settings.Php.Versions is null
            ? new Dictionary<string, PhpVersionSettings>()
            : new Dictionary<string, PhpVersionSettings>(settings.Php.Versions);

        if (!versions.TryGetValue(versionId, out var versionSettings))
        {
            versionSettings = SettingsDefaults.DefaultPhpVersionSettings();
            versions[versionId] = versionSettings;
            _settingsStore.UpdatePhp(settings.Php with { Versions = versions });
        }

        return MergeDiscoveredExtensions(versionId, versionSettings);
    }

    public IReadOnlyList<PhpExtensionState> ListExtensionStates(string versionId)
    {
        var installed = _registry.GetById(versionId);
        if (installed is null)
        {
            return [];
        }

        var settings = _settingsStore.Load();
        var versionSettings = MergeDiscoveredExtensions(versionId, ResolveVersionSettings(settings, versionId));
        var manifest = _manifestStore.Load();
        var root = PhpExtensionPolicy.ResolvePackageRoot(installed.InstallPath);
        var extDir = Path.Combine(root, "ext");
        var discovered = PhpExtensionPolicy.DiscoverExtensions(installed.InstallPath).ToList();
        var peclIds = manifest.Extensions
            .Where(e => string.Equals(e.Kind, "pecl", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var names = discovered
            .Concat(versionSettings.Extensions.Keys.Where(name => PhpExtensionPolicy.ExtensionDllExists(extDir, name)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var output = new List<PhpExtensionState>(names.Count);
        foreach (var name in names)
        {
            var entry = manifest.Extensions.FirstOrDefault(e => string.Equals(e.Id, name, StringComparison.OrdinalIgnoreCase));
            var enabled = versionSettings.Extensions.TryGetValue(name, out var pref) ? pref : PhpExtensionPolicy.DefaultExtensionPreference(name, manifest, _registry, settings);
            var available = PhpExtensionPolicy.ExtensionDllExists(extDir, name);
            var blockedReason = PhpExtensionPolicy.ExtensionBlockedReason(name, entry, _registry, settings, extDir);
            var serviceReady = blockedReason is null || !blockedReason.StartsWith("Requires", StringComparison.OrdinalIgnoreCase);
            var effective = enabled && available && blockedReason is null;
            var kind = entry?.Kind ?? (peclIds.Contains(name) ? "pecl" : "bundled");

            output.Add(new PhpExtensionState(
                name,
                entry?.Label ?? name,
                entry?.Description,
                enabled,
                effective,
                available,
                kind,
                serviceReady,
                blockedReason,
                CanToggle: available && blockedReason is null));
        }

        return output;
    }

    public IReadOnlyList<InstallablePeclExtension> ListInstallablePecl(string versionId)
    {
        var installed = _registry.GetById(versionId);
        if (installed is null)
        {
            return [];
        }

        var manifest = _manifestStore.Load();
        var root = PhpExtensionPolicy.ResolvePackageRoot(installed.InstallPath);
        var extDir = Path.Combine(root, "ext");
        var output = new List<InstallablePeclExtension>();

        foreach (var entry in manifest.Extensions.Where(e => string.Equals(e.Kind, "pecl", StringComparison.OrdinalIgnoreCase)))
        {
            if (entry.WindowsSupported == false)
            {
                continue;
            }

            if (PhpExtensionPolicy.ExtensionDllExists(extDir, entry.Id))
            {
                continue;
            }

            if (_manifestStore.ResolvePeclBuild(entry.Id, versionId) is null
                && _manifestStore.ResolvePieInstallSpec(entry.Id, versionId) is null)
            {
                continue;
            }

            output.Add(new InstallablePeclExtension(entry.Id, entry.Label, entry.Description));
        }

        return output;
    }

    public void ToggleExtension(string versionId, string extensionId, bool enabled)
    {
        var settings = _settingsStore.Load();
        var versions = settings.Php.Versions is null
            ? new Dictionary<string, PhpVersionSettings>()
            : new Dictionary<string, PhpVersionSettings>(settings.Php.Versions);
        var versionSettings = MergeDiscoveredExtensions(versionId, ResolveVersionSettings(settings, versionId));
        versionSettings.Extensions[extensionId] = enabled;
        versions[versionId] = versionSettings;
        settings = _settingsStore.UpdatePhp(settings.Php with { Versions = versions });
        _configWriter.WritePhpConfig(settings, versionId);
    }

    public void EnableAllReady(string versionId)
    {
        foreach (var state in ListExtensionStates(versionId).Where(s => s.CanToggle && !s.Enabled))
        {
            ToggleExtension(versionId, state.Name, enabled: true);
        }
    }

    public void ResetExtensions(string versionId)
    {
        var settings = _settingsStore.Load();
        var versions = settings.Php.Versions is null
            ? new Dictionary<string, PhpVersionSettings>()
            : new Dictionary<string, PhpVersionSettings>(settings.Php.Versions);
        var versionSettings = SettingsDefaults.DefaultPhpVersionSettings();
        var manifest = _manifestStore.Load();
        var installed = _registry.GetById(versionId);
        if (installed is null)
        {
            return;
        }

        PhpIniMerge.EnsurePhpIniTemplate(_paths.ConfigRoot, installed.InstallPath, versionId);
        PhpIniMerge.RestoreIniFromTemplate(
            _paths.ConfigRoot,
            versionId,
            PhpConfigPaths.GetDefaultIniPath(_paths.ConfigRoot, versionId));

        var discovered = PhpExtensionPolicy.DiscoverExtensions(installed.InstallPath);
        versionSettings.Extensions = discovered.ToDictionary(
            static name => name,
            name => PhpExtensionPolicy.DefaultExtensionPreference(name, manifest, _registry, settings),
            StringComparer.OrdinalIgnoreCase);

        versions[versionId] = versionSettings;
        settings = _settingsStore.UpdatePhp(settings.Php with { Versions = versions });
        _configWriter.WritePhpConfig(settings, versionId);
    }

    public void SaveVersionSettings(string versionId, PhpVersionSettings patch)
    {
        var settings = _settingsStore.Load();
        var versions = settings.Php.Versions is null
            ? new Dictionary<string, PhpVersionSettings>()
            : new Dictionary<string, PhpVersionSettings>(settings.Php.Versions);
        var current = MergeDiscoveredExtensions(versionId, ResolveVersionSettings(settings, versionId));

        versions[versionId] = PhpVersionSettingsSanitizer.Sanitize(current with
        {
            MemoryLimit = patch.MemoryLimit,
            MaxExecutionTime = patch.MaxExecutionTime,
            UploadMaxFilesize = patch.UploadMaxFilesize,
            PostMaxSize = patch.PostMaxSize,
            MaxInputTime = patch.MaxInputTime,
            MaxInputVars = patch.MaxInputVars,
            DefaultSocketTimeout = patch.DefaultSocketTimeout,
            RealpathCacheSize = patch.RealpathCacheSize,
            RealpathCacheTtl = patch.RealpathCacheTtl,
            OpcacheEnabled = patch.OpcacheEnabled,
            OpcacheEnableCli = patch.OpcacheEnableCli,
            OpcacheValidateTimestamps = patch.OpcacheValidateTimestamps,
            OpcacheRevalidateFreq = patch.OpcacheRevalidateFreq,
            OpcacheMemoryConsumption = patch.OpcacheMemoryConsumption,
            OpcacheMaxAcceleratedFiles = patch.OpcacheMaxAcceleratedFiles,
            ManageIniManually = patch.ManageIniManually,
            DisplayErrors = patch.DisplayErrors,
            HideWarnings = patch.HideWarnings,
            HideDeprecated = patch.HideDeprecated,
            LogErrors = patch.LogErrors
        });

        settings = _settingsStore.UpdatePhp(settings.Php with { Versions = versions });
        _configWriter.WritePhpConfig(settings, versionId);
    }

    public void SaveRuntimeSettings(string fpmHost, int fpmPort, int fpmPoolSize)
    {
        var settings = _settingsStore.Load();
        settings = _settingsStore.UpdatePhp(settings.Php with
        {
            FpmHost = fpmHost.Trim(),
            FpmPort = fpmPort,
            FpmPoolSize = Math.Clamp(fpmPoolSize, 1, 8)
        });
        _configWriter.WriteAllPhpConfigs(settings);
    }

    public void MarkExtensionEnabled(string versionId, string extensionId)
    {
        ToggleExtension(versionId, extensionId, enabled: true);
    }

    public void ApplyImportedProfile(string versionId, PhpVersionSettings profile)
    {
        var settings = _settingsStore.Load();
        var versions = settings.Php.Versions is null
            ? new Dictionary<string, PhpVersionSettings>()
            : new Dictionary<string, PhpVersionSettings>(settings.Php.Versions);

        var merged = MergeDiscoveredExtensions(versionId, profile);
        versions[versionId] = merged with
        {
            MemoryLimit = profile.MemoryLimit,
            MaxExecutionTime = profile.MaxExecutionTime,
            UploadMaxFilesize = profile.UploadMaxFilesize,
            PostMaxSize = profile.PostMaxSize,
            DisplayErrors = profile.DisplayErrors,
            HideWarnings = profile.HideWarnings,
            HideDeprecated = profile.HideDeprecated,
            LogErrors = profile.LogErrors,
            Extensions = new Dictionary<string, bool>(profile.Extensions, StringComparer.OrdinalIgnoreCase),
            IniOverrides = profile.IniOverrides is null
                ? []
                : new Dictionary<string, string>(profile.IniOverrides, StringComparer.Ordinal)
        };

        settings = _settingsStore.UpdatePhp(settings.Php with { Versions = versions });
        _configWriter.WritePhpConfig(settings, versionId);
    }

    private PhpVersionSettings ResolveVersionSettings(AppSettings settings, string versionId)
    {
        if (settings.Php.Versions is not null && settings.Php.Versions.TryGetValue(versionId, out var version))
        {
            return version;
        }

        return SettingsDefaults.DefaultPhpVersionSettings();
    }

    private PhpVersionSettings MergeDiscoveredExtensions(string versionId, PhpVersionSettings versionSettings)
    {
        var installed = _registry.GetById(versionId);
        if (installed is null)
        {
            return versionSettings;
        }

        var settings = _settingsStore.Load();
        var manifest = _manifestStore.Load();
        var extensions = new Dictionary<string, bool>(versionSettings.Extensions, StringComparer.OrdinalIgnoreCase);
        foreach (var name in PhpExtensionPolicy.DiscoverExtensions(installed.InstallPath))
        {
            if (!extensions.ContainsKey(name))
            {
                extensions[name] = PhpExtensionPolicy.DefaultExtensionPreference(name, manifest, _registry, settings);
            }
        }

        return versionSettings with { Extensions = extensions };
    }
}
