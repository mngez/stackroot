using System.Diagnostics;
using System.Text;
using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Catalog;

public sealed class ComposerManager
{
    private readonly InstallRegistryStore _registry;
    private readonly PackageCatalogStore _catalog;
    private readonly PackageInstaller _installer;
    private readonly IDiagnosticsReporter? _diagnostics;

    public ComposerManager(
        InstallRegistryStore registry,
        PackageCatalogStore catalog,
        PackageInstaller installer,
        IDiagnosticsReporter? diagnostics = null)
    {
        _registry = registry;
        _catalog = catalog;
        _installer = installer;
        _diagnostics = diagnostics;
    }

    private PackageEntry? ResolveCatalogEntry()
    {
        return _catalog.List(PackageType.Composer)
            .OrderByDescending(e => e.Version, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private InstalledPackage? ResolveInstalled()
    {
        return _registry.List(PackageType.Composer)
            .OrderByDescending(p => p.Version, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    public string? InstalledPharPath
    {
        get
        {
            var pkg = ResolveInstalled();
            if (pkg is null) return null;
            var phar = Path.Combine(pkg.InstallPath, "composer.phar");
            return File.Exists(phar) ? phar : null;
        }
    }

    public bool IsInstalled => InstalledPharPath is not null;

    public async Task<string> EnsureInstalledAsync(CancellationToken cancel = default)
    {
        if (!IsInstalled)
        {
            var entry = ResolveCatalogEntry()
                ?? throw new InvalidOperationException("Composer not found in catalog.");
            _diagnostics?.LogActivity("Composer", "Installing Composer from catalog…");
            await _installer.InstallAsync(entry, (InstallProgressCallback?)null, cancel).ConfigureAwait(false);
            _diagnostics?.LogActivity("Composer", "Composer installed");
        }

        return InstalledPharPath
            ?? throw new InvalidOperationException("Composer installation failed.");
    }

    /// <summary>
    /// Resolves the best way to run Composer: returns (exe, prefixArgs) tuple.
    /// </summary>
    public async Task<ComposerRunInfo> ResolveRunInfoAsync(string phpExe, CancellationToken cancel = default)
    {
        var pharPath = await EnsureInstalledAsync(cancel).ConfigureAwait(false);
        return new ComposerRunInfo(phpExe, [pharPath]);
    }
}

public sealed class ComposerRunInfo
{
    public string FileName { get; }
    public IReadOnlyList<string> PrefixArguments { get; }

    public ComposerRunInfo(string fileName, IReadOnlyList<string> prefixArgs)
    {
        FileName = fileName;
        PrefixArguments = prefixArgs;
    }
}
