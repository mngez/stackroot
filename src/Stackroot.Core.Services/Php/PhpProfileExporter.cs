using System.Text.Json;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Abstractions.DataDocuments;
using Stackroot.Core.Catalog;
using Stackroot.Core.IO;

namespace Stackroot.Core.Services.Php;

public sealed class PhpProfileExporter
{
    private readonly PhpExtensionManager _extensionManager;
    private readonly PackageCatalogStore _catalogStore;
    private readonly InstallRegistryStore _registryStore;

    public PhpProfileExporter(
        PhpExtensionManager extensionManager,
        PackageCatalogStore catalogStore,
        InstallRegistryStore registryStore)
    {
        _extensionManager = extensionManager;
        _catalogStore = catalogStore;
        _registryStore = registryStore;
    }

    public PhpProfileDocument Export(string versionId, string? stackrootVersion = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);

        if (_registryStore.GetById(versionId) is null)
        {
            throw new InvalidOperationException($"PHP version '{versionId}' is not installed.");
        }

        var versionSettings = _extensionManager.EnsureVersionSettings(versionId);
        var merged = _extensionManager.ListExtensionStates(versionId)
            .ToDictionary(static state => state.Name, static state => state.Enabled, StringComparer.OrdinalIgnoreCase);

        foreach (var pair in versionSettings.Extensions)
        {
            merged[pair.Key] = pair.Value;
        }

        var catalogEntry = _catalogStore.GetById(versionId);

        return new PhpProfileDocument
        {
            SchemaVersion = DataDocumentSchemas.PhpProfile,
            ExportedAt = DateTimeOffset.UtcNow.ToString("O"),
            StackrootVersion = stackrootVersion,
            TargetVersionId = versionId,
            TargetVersionLabel = catalogEntry?.Label ?? versionId,
            Runtime = new PhpProfileRuntime
            {
                MemoryLimit = versionSettings.MemoryLimit,
                MaxExecutionTime = versionSettings.MaxExecutionTime,
                UploadMaxFilesize = versionSettings.UploadMaxFilesize,
                PostMaxSize = versionSettings.PostMaxSize,
                DisplayErrors = versionSettings.DisplayErrors,
                HideWarnings = versionSettings.HideWarnings,
                HideDeprecated = versionSettings.HideDeprecated,
                LogErrors = versionSettings.LogErrors
            },
            Extensions = merged,
            IniOverrides = versionSettings.IniOverrides is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(versionSettings.IniOverrides, StringComparer.Ordinal)
        };
    }

    public string SerializeToJson(PhpProfileDocument document) =>
        JsonSerializer.Serialize(document, JsonSerializerConfig.Default);

    public PhpProfilesBundleDocument ExportAll(string? stackrootVersion = null)
    {
        var profiles = _registryStore.List(PackageType.Php)
            .Select(static installed => installed.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
            .Select(id => Export(id, stackrootVersion))
            .ToList();

        if (profiles.Count == 0)
        {
            throw new InvalidOperationException("No PHP versions are installed to export.");
        }

        return new PhpProfilesBundleDocument
        {
            SchemaVersion = DataDocumentSchemas.PhpProfilesBundle,
            ExportedAt = DateTimeOffset.UtcNow.ToString("O"),
            StackrootVersion = stackrootVersion,
            Profiles = profiles
        };
    }

    public string SerializeBundleToJson(PhpProfilesBundleDocument bundle) =>
        JsonSerializer.Serialize(bundle, JsonSerializerConfig.Default);
}
