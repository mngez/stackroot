using System.Collections.Frozen;

namespace Stackroot.Core.Sites.Installers;

/// <summary>
/// Auto-collects every <see cref="ISiteInstaller"/> registered in DI
/// and indexes them by <see cref="ISiteInstaller.TemplateId"/>.
/// </summary>
public sealed class SiteInstallerRegistry
{
    private readonly FrozenDictionary<string, ISiteInstaller> _map;

    public SiteInstallerRegistry(IEnumerable<ISiteInstaller> installers)
    {
        _map = installers.ToFrozenDictionary(
            x => x.TemplateId,
            x => x,
            StringComparer.OrdinalIgnoreCase);
    }

    public ISiteInstaller? Get(string templateId) =>
        _map.TryGetValue(templateId, out var installer) ? installer : null;

    public IReadOnlyList<ISiteInstaller> List() => _map.Values.ToList();

    public bool HasInstaller(string templateId) => _map.ContainsKey(templateId);
}
