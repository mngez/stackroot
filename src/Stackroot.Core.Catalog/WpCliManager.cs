using System.Diagnostics;
using System.Text;
using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Catalog;

public sealed class WpCliManager
{
    private readonly InstallRegistryStore _registry;
    private readonly PackageCatalogStore _catalog;
    private readonly PackageInstaller _installer;
    private readonly IDiagnosticsReporter? _diagnostics;

    public WpCliManager(
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
        // Pick the latest version from catalog
        return _catalog.List(PackageType.WpCli)
            .OrderByDescending(e => e.Version, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private InstalledPackage? ResolveInstalled()
    {
        return _registry.List(PackageType.WpCli)
            .OrderByDescending(p => p.Version, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    public string? InstalledPharPath
    {
        get
        {
            var pkg = ResolveInstalled();
            if (pkg is null) return null;
            var phar = Path.Combine(pkg.InstallPath, "wp-cli.phar");
            return File.Exists(phar) ? phar : null;
        }
    }

    public bool IsInstalled => InstalledPharPath is not null;

    public async Task<string> EnsureInstalledAsync(CancellationToken cancel = default)
    {
        if (!IsInstalled)
        {
            var entry = ResolveCatalogEntry()
                ?? throw new InvalidOperationException("wp-cli not found in catalog.");
            _diagnostics?.LogActivity("WpCli", "Installing wp-cli from catalog…");
            await _installer.InstallAsync(entry, (InstallProgressCallback?)null, cancel).ConfigureAwait(false);
            _diagnostics?.LogActivity("WpCli", "wp-cli installed");
        }

        return InstalledPharPath
            ?? throw new InvalidOperationException("wp-cli installation failed.");
    }

    public async Task<(int ExitCode, string Output)> RunAsync(
        string phpExe, string workingDir, string arguments,
        Action<string>? onOutput = null, CancellationToken cancel = default)
    {
        var phar = await EnsureInstalledAsync(cancel).ConfigureAwait(false);

        var psi = new ProcessStartInfo
        {
            FileName = phpExe,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add(phar);
        foreach (var arg in SplitArgs(arguments))
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start wp-cli.");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        var readStdout = Task.Run(() =>
        {
            string? line;
            while ((line = process.StandardOutput.ReadLine()) is not null)
            {
                stdout.AppendLine(line);
                onOutput?.Invoke(line);
            }
        }, cancel);

        var readStderr = Task.Run(() =>
        {
            string? line;
            while ((line = process.StandardError.ReadLine()) is not null)
                stderr.AppendLine(line);
        }, cancel);

        await process.WaitForExitAsync(cancel).ConfigureAwait(false);
        await Task.WhenAll(readStdout, readStderr).ConfigureAwait(false);

        var output = stdout.ToString();
        if (process.ExitCode != 0 && stderr.Length > 0)
            output += "\n" + stderr;

        return (process.ExitCode, output.Trim());
    }

    private static IEnumerable<string> SplitArgs(string arguments)
    {
        var inQuotes = false;
        var current = new StringBuilder();
        foreach (var c in arguments)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (c == ' ' && !inQuotes)
            {
                if (current.Length > 0) { yield return current.ToString(); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) yield return current.ToString();
    }
}
