using System.Text.Json;
using System.Text.Json.Serialization;
using Stackroot.Core.IO;
using Stackroot.Core.Settings;
using Stackroot.Core.Sites;
using Stackroot.Core.Sites.Models;
namespace Stackroot.Core.Sites.Persistence;

public sealed class SiteStore
{
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly SettingsStore _settingsStore;
    private readonly JsonFileStore _jsonStore;

    public SiteStore(string dataRoot, SettingsStore settingsStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentNullException.ThrowIfNull(settingsStore);
        DataRoot = dataRoot;
        _settingsStore = settingsStore;
        _jsonStore = new JsonFileStore(_jsonOptions);
    }

    public string DataRoot { get; }
    public string SitesFilePath => Path.Combine(DataRoot, "sites.json");

    public SitesRegistry Load()
    {
        if (!File.Exists(SitesFilePath))
        {
            return new SitesRegistry();
        }

        try
        {
            var json = File.ReadAllText(SitesFilePath);
            var registry = JsonSerializer.Deserialize<SitesRegistry>(json, _jsonOptions) ?? new SitesRegistry();
            registry.SchemaVersion = SitesRegistry.CurrentSchemaVersion;
            registry.Sites ??= [];
            registry.Sites = registry.Sites.Select(NormalizeSite).ToList();
            return registry;
        }
        catch (Exception ex)
        {
            var backupPath = TryBackupUnreadableFile(SitesFilePath);
            var backupMessage = string.IsNullOrWhiteSpace(backupPath)
                ? string.Empty
                : $" A backup was saved to '{backupPath}'.";
            throw new InvalidDataException($"Could not read Stackroot sites registry '{SitesFilePath}'.{backupMessage}", ex);
        }
    }

    public bool TryLoad(out SitesRegistry registry, out Exception? error)
    {
        try
        {
            registry = Load();
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            registry = new SitesRegistry();
            error = ex;
            return false;
        }
    }

    public void Save(SitesRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        Directory.CreateDirectory(DataRoot);
        registry.SchemaVersion = SitesRegistry.CurrentSchemaVersion;
        registry.Sites ??= [];
        _jsonStore.WriteAtomic(SitesFilePath, registry);
    }

    public IReadOnlyList<Site> List() => Load().Sites.Select(CloneSite).ToList();

    public Site? GetById(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var site = Load().Sites.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
        return site is null ? null : CloneSite(site);
    }

    public Site? GetByDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            return null;
        }

        var normalized = domain.Trim();
        return Load().Sites
            .Where(site => SiteDomainNames.GetServerNames(site)
                .Any(name => string.Equals(name, normalized, StringComparison.OrdinalIgnoreCase)))
            .Select(CloneSite)
            .FirstOrDefault();
    }

    public Site Create(CreateSiteInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateCreateInput(input);

        var registry = Load();
        var domain = SitePaths.BuildDomain(input.Domain, input.DomainSuffix);
        ValidateDomainAliases(domain, input.DomainAliases);
        if (registry.Sites.Any(site => SiteDomainNames.SharesBoundName(
                new Site { Id = string.Empty, Domain = domain, DomainAliases = input.DomainAliases },
                site)))
        {
            throw new InvalidOperationException($"Site already exists: {domain}");
        }

        var template = SiteTemplates.Resolve(input.Template);
        var settings = _settingsStore.Load();
        var pathMode = SitePaths.IsCustomPathMode(input) ? "custom" : "default";
        var now = DateTimeOffset.UtcNow.ToString("O");
        var site = new Site
        {
            Id = BuildSiteId(domain),
            Name = string.IsNullOrWhiteSpace(input.Name) ? domain : input.Name.Trim(),
            Domain = domain,
            DomainAliases = SiteDomainNames.NormalizeAliases(domain, input.DomainAliases),
            Template = template.Id,
            PhpVersionId = string.IsNullOrWhiteSpace(input.PhpVersionId) ? null : input.PhpVersionId.Trim(),
            NodeVersionId = string.IsNullOrWhiteSpace(input.NodeVersionId) ? null : input.NodeVersionId.Trim(),
            Path = SitePaths.ResolveSitePath(input, settings.General.WwwPath),
            DocumentRoot = template.DocumentRoot,
            PathMode = pathMode,
            Enabled = input.Enabled ?? true,
            Featured = input.Featured,
            ForceHttps = null,
            DevProxies = CloneProxies(template.DevProxies),
            CreatedAt = now,
            UpdatedAt = now
        };

        registry.Sites.Add(site);
        Save(registry);
        return CloneSite(site);
    }

    public Site Update(string id, UpdateSiteInput patch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(patch);

        var registry = Load();
        var index = registry.Sites.FindIndex(site => site.Id == id);
        if (index < 0)
        {
            throw new KeyNotFoundException($"Site not found: {id}");
        }

        var current = registry.Sites[index];
        var updatedDomain = string.IsNullOrWhiteSpace(patch.Domain) ? current.Domain : patch.Domain.Trim();
        var updatedAliases = patch.DomainAliases is null
            ? current.DomainAliases
            : SiteDomainNames.NormalizeAliases(updatedDomain, patch.DomainAliases);
        ValidateDomainAliases(updatedDomain, updatedAliases);
        var templateChanged = !string.IsNullOrWhiteSpace(patch.Template) &&
                              !string.Equals(current.Template, patch.Template, StringComparison.OrdinalIgnoreCase);
        var template = SiteTemplates.Resolve(templateChanged ? patch.Template : current.Template);

        var candidate = new Site
        {
            Id = current.Id,
            Domain = updatedDomain,
            DomainAliases = updatedAliases
        };
        var duplicateDomain = registry.Sites.Any(site => SiteDomainNames.SharesBoundName(candidate, site));
        if (duplicateDomain)
        {
            throw new InvalidOperationException($"Site already exists: {updatedDomain}");
        }

        var newPath = string.IsNullOrWhiteSpace(patch.Path) ? current.Path : patch.Path.Trim();
        var newPathMode = !string.IsNullOrWhiteSpace(patch.PathMode)
            ? patch.PathMode.Trim()
            : current.PathMode;

        var updated = new Site
        {
            Id = current.Id,
            Name = string.IsNullOrWhiteSpace(patch.Name) ? current.Name : patch.Name.Trim(),
            Domain = updatedDomain,
            DomainAliases = updatedAliases,
            Template = templateChanged ? template.Id : current.Template,
            PhpVersionId = patch.PhpVersionId is null ? current.PhpVersionId : patch.PhpVersionId.Trim(),
            NodeVersionId = patch.NodeVersionId is null ? current.NodeVersionId : (patch.NodeVersionId.Trim() is { Length: > 0 } nv ? nv : null),
            Path = newPath,
            DocumentRoot = string.IsNullOrWhiteSpace(patch.DocumentRoot)
                ? (templateChanged ? template.DocumentRoot : current.DocumentRoot)
                : patch.DocumentRoot.Trim(),
            PathMode = newPathMode,
            Enabled = patch.Enabled ?? current.Enabled,
            Featured = patch.Featured ?? current.Featured,
            ForceHttps = patch.ForceHttps ?? current.ForceHttps,
            DevProxies = patch.DevProxies is null
                ? (templateChanged ? CloneProxies(template.DevProxies) : CloneProxies(current.DevProxies))
                : CloneProxies(patch.DevProxies),
            CustomCommands = current.CustomCommands?.Select(c => new SiteCustomCommand
            {
                Id = c.Id, Label = c.Label, Command = c.Command, Runtime = c.Runtime
            }).ToList(),
            CreatedAt = current.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O")
        };

        registry.Sites[index] = updated;
        Save(registry);
        return CloneSite(updated);
    }

    public Site? Remove(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var registry = Load();
        var site = registry.Sites.FirstOrDefault(s => s.Id == id);
        if (site is null)
        {
            return null;
        }

        registry.Sites.RemoveAll(s => s.Id == id);
        Save(registry);
        return CloneSite(site);
    }

    private static string BuildSiteId(string domain)
    {
        var chars = domain.Trim().ToLowerInvariant().Select(c =>
            char.IsLetterOrDigit(c) ? c : '-');
        return string.Concat(chars).Trim('-');
    }

    private Site NormalizeSite(Site site)
    {
        var settings = _settingsStore.Load();
        var template = SiteTemplates.Resolve(site.Template);
        var domain = site.Domain?.Trim().ToLowerInvariant() ?? string.Empty;
        var pathMode = string.IsNullOrWhiteSpace(site.PathMode) ? "default" : site.PathMode.Trim();
        var path = site.Path?.Trim() ?? string.Empty;
        if (string.Equals(pathMode, "default", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(domain))
        {
            path = Path.Combine(SitePaths.EffectiveWwwPath(settings.General.WwwPath), domain);
        }

        return new Site
        {
            Id = string.IsNullOrWhiteSpace(site.Id) ? BuildSiteId(domain) : site.Id.Trim(),
            Name = site.Name?.Trim() ?? domain,
            Domain = domain,
            DomainAliases = SiteDomainNames.NormalizeAliases(domain, site.DomainAliases),
            Template = template.Id,
            PhpVersionId = string.IsNullOrWhiteSpace(site.PhpVersionId) ? null : site.PhpVersionId.Trim(),
            NodeVersionId = string.IsNullOrWhiteSpace(site.NodeVersionId) ? null : site.NodeVersionId.Trim(),
            Path = path,
            DocumentRoot = string.IsNullOrWhiteSpace(site.DocumentRoot) ? template.DocumentRoot : site.DocumentRoot.Trim(),
            PathMode = pathMode,
            Enabled = site.Enabled,
            Featured = site.Featured,
            ForceHttps = site.ForceHttps,
            DevProxies = CloneProxies(site.DevProxies),
            CustomCommands = site.CustomCommands?.Select(c => new SiteCustomCommand
            {
                Id = c.Id, Label = c.Label, Command = c.Command, Runtime = c.Runtime
            }).ToList(),
            CreatedAt = string.IsNullOrWhiteSpace(site.CreatedAt) ? DateTimeOffset.UtcNow.ToString("O") : site.CreatedAt,
            UpdatedAt = string.IsNullOrWhiteSpace(site.UpdatedAt) ? DateTimeOffset.UtcNow.ToString("O") : site.UpdatedAt
        };
    }

    private Site CloneSite(Site site) => NormalizeSite(site);

    private static List<SiteDevProxy>? CloneProxies(IEnumerable<SiteDevProxy>? proxies)
    {
        if (proxies is null)
        {
            return null;
        }

        return proxies.Select(proxy => new SiteDevProxy
        {
            Id = proxy.Id,
            Name = proxy.Name,
            Enabled = proxy.Enabled,
            LocationPath = proxy.LocationPath,
            TargetUrl = proxy.TargetUrl,
            Websocket = proxy.Websocket
        }).ToList();
    }

    private static void ValidateCreateInput(CreateSiteInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Domain))
        {
            throw new InvalidOperationException("Domain is required.");
        }
    }

    private static void ValidateDomainAliases(string primaryDomain, IEnumerable<string>? aliases)
    {
        var error = SiteDomainNames.ValidateAliases(primaryDomain, aliases);
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }
    }

    private static string? TryBackupUnreadableFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var backupPath = $"{path}.invalid-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.bak";
            File.Copy(path, backupPath, overwrite: false);
            return backupPath;
        }
        catch
        {
            return null;
        }
    }
}
