using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Settings;

namespace Stackroot.Core.Node;

public sealed class NodeManager
{
    private static readonly Regex VersionRegex = new(@"(?<!\d)(\d+\.\d+\.\d+)(?!\d)", RegexOptions.Compiled);

    private readonly StackrootPaths _paths;
    private readonly InstallRegistryStore _registry;
    private readonly PackageCatalogStore _catalog;
    private readonly PackageInstaller _installer;
    private readonly SettingsStore _settings;

    public NodeManager(
        StackrootPaths paths,
        InstallRegistryStore registry,
        PackageCatalogStore catalog,
        PackageInstaller installer,
        SettingsStore settings)
    {
        _paths = paths;
        _registry = registry;
        _catalog = catalog;
        _installer = installer;
        _settings = settings;
    }

    public Task<NodeRuntimeStatus> InstallNvmAsync(CancellationToken cancellationToken = default)
        => InstallNvmPackageAsync(cancellationToken);

    public async Task<NodeRuntimeStatus> GetRuntimeStatusAsync(CancellationToken cancellationToken = default)
    {
        var nvmPackage = GetInstalledNvmPackage();
        if (nvmPackage is null)
        {
            return new NodeRuntimeStatus
            {
                NvmInstalled = false,
                Message = "nvm-windows package is not installed."
            };
        }

        var nvmExe = ResolveNvmExecutable(nvmPackage.InstallPath);
        if (nvmExe is null)
        {
            return new NodeRuntimeStatus
            {
                NvmInstalled = false,
                Message = "nvm.exe was not found in the installed nvm package."
            };
        }

        EnsureNvmConfigured(nvmExe);

        var nvmVersionResult = await RunNvmAsync(nvmExe, "version", cancellationToken);
        var listedVersions = await ListVersionsFromNvmAsync(nvmExe, cancellationToken);
        var installedVersions = MergeInstalledVersions(listedVersions);

        var activeVersion = await ResolveActiveNodeVersionAsync(cancellationToken)
            ?? _settings.Load().Node.ActiveVersion;
        var nodeExePath = ResolveNodeExecutablePath(activeVersion);
        SyncActiveNodeSetting(activeVersion);

        return new NodeRuntimeStatus
        {
            NvmInstalled = true,
            NvmVersion = FirstVersionOrRaw(nvmVersionResult.Output),
            ActiveVersion = activeVersion,
            NodeExecutablePath = nodeExePath,
            InstalledVersions = installedVersions,
            Message = nvmVersionResult.ExitCode == 0
                ? null
                : string.Join(Environment.NewLine, new[] { nvmVersionResult.Output }.Where(static m => !string.IsNullOrWhiteSpace(m)))
        };
    }

    public async Task<NodeRuntimeStatus> InstallVersionAsync(string version, CancellationToken cancellationToken = default)
    {
        NodeVersionCatalog.ValidateVersion(version);
        var normalized = NodePaths.NormalizeVersion(version);

        var nvmExe = await EnsureNvmReadyAsync(cancellationToken);
        PinVersion(normalized);

        var installResult = await RunNvmAsync(nvmExe, $"install {normalized}", cancellationToken);
        if (installResult.ExitCode != 0 && !LooksLikeNvmSuccess(installResult.Output))
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(installResult.Output)
                ? $"nvm install {normalized} failed."
                : installResult.Output);
        }

        var resolved = ResolveInstalledVersionAfterInstall(normalized, installResult.Output);
        if (resolved is null)
        {
            throw new InvalidOperationException(
                $"nvm reported success but Node {normalized} was not found under {NodePaths.VersionsRoot(_paths)}.");
        }

        return await UseVersionAsync(resolved, cancellationToken);
    }

    public async Task<NodeRuntimeStatus> UseVersionAsync(string version, CancellationToken cancellationToken = default)
    {
        NodeVersionCatalog.ValidateVersion(version);
        var normalized = NodePaths.NormalizeVersion(version);

        var nvmExe = await EnsureNvmReadyAsync(cancellationToken);
        var useResult = await RunNvmAsync(nvmExe, $"use {normalized}", cancellationToken);
        if (useResult.ExitCode != 0 && !LooksLikeNvmSuccess(useResult.Output))
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(useResult.Output)
                ? $"nvm use {normalized} failed."
                : useResult.Output);
        }

        var settings = _settings.Load();
        settings.Node.ActiveVersion = normalized;
        _settings.Save(settings);

        return await GetRuntimeStatusAsync(cancellationToken);
    }

    public async Task<NodeRuntimeStatus> UninstallVersionAsync(string version, CancellationToken cancellationToken = default)
    {
        NodeVersionCatalog.ValidateVersion(version);
        var normalized = NodePaths.NormalizeVersion(version);

        var nvmExe = await EnsureNvmReadyAsync(cancellationToken);
        var uninstallResult = await RunNvmAsync(nvmExe, $"uninstall {normalized}", cancellationToken);
        if (uninstallResult.ExitCode != 0 && !LooksLikeNvmSuccess(uninstallResult.Output))
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(uninstallResult.Output)
                ? $"nvm uninstall {normalized} failed."
                : uninstallResult.Output);
        }

        var settings = _settings.Load();
        var pinned = settings.Node.PinnedVersions?.Where(v =>
                !string.Equals(NodePaths.NormalizeVersion(v), normalized, StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];
        _settings.UpdateNode(settings.Node with { PinnedVersions = pinned });

        if (string.Equals(settings.Node.ActiveVersion, normalized, StringComparison.OrdinalIgnoreCase))
        {
            _settings.UpdateNode(_settings.Load().Node with { ActiveVersion = null });
        }

        return await GetRuntimeStatusAsync(cancellationToken);
    }

    public void ClearNvmSettings()
    {
        var settings = _settings.Load();
        settings.Node.NvmPackageId = null;
        settings.Node.ActiveVersion = null;
        settings.Node.PinnedVersions = [];
        _settings.Save(settings);
    }

    public void ConfigureAfterNvmInstall(string installPath)
    {
        var nvmExe = ResolveNvmExecutable(installPath)
            ?? throw new InvalidOperationException("nvm.exe not found in installed package.");
        EnsureNvmConfigured(nvmExe);
    }

    private async Task<string> EnsureNvmReadyAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var nvmPackage = GetInstalledNvmPackage()
            ?? throw new InvalidOperationException("Install nvm-windows from the Node page first.");

        var nvmExe = ResolveNvmExecutable(nvmPackage.InstallPath)
            ?? throw new FileNotFoundException("nvm.exe was not found after installation.");

        EnsureNvmConfigured(nvmExe);
        return await Task.FromResult(nvmExe);
    }

    public InstalledPackage? GetInstalledNvmPackage()
    {
        var packageId = _settings.Load().Node.NvmPackageId;
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return null;
        }

        return _registry.GetById(packageId);
    }

    private async Task<NodeRuntimeStatus> InstallNvmPackageAsync(CancellationToken cancellationToken)
    {
        if (GetInstalledNvmPackage() is not null)
        {
            return await GetRuntimeStatusAsync(cancellationToken);
        }

        var nvmPackage = _catalog.List(PackageType.Nvm)
            .OrderByDescending(p => p.Version, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (nvmPackage is null)
        {
            return new NodeRuntimeStatus
            {
                NvmInstalled = false,
                Message = "nvm-windows package not found in catalog."
            };
        }

        await _installer.InstallAsync(nvmPackage, cancellationToken: cancellationToken);

        var installed = _registry.GetById(nvmPackage.Id);
        if (installed is not null)
        {
            ConfigureAfterNvmInstall(installed.InstallPath);
        }

        var settings = _settings.Load();
        _settings.UpdateNode(settings.Node with { NvmPackageId = nvmPackage.Id });

        return await GetRuntimeStatusAsync(cancellationToken);
    }

    private string? ResolveInstalledVersionAfterInstall(string requested, string nvmOutput)
    {
        var normalized = NodePaths.NormalizeVersion(requested);
        if (Directory.Exists(NodePaths.VersionDirectory(_paths, normalized)))
        {
            return normalized;
        }

        foreach (Match match in VersionRegex.Matches(nvmOutput))
        {
            var candidate = match.Groups[1].Value;
            if (Directory.Exists(NodePaths.VersionDirectory(_paths, candidate)))
            {
                return candidate;
            }
        }

        return MergeInstalledVersions([])
            .Where(version => VersionMatchesRequest(version, normalized))
            .OrderByDescending(static version => version, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool VersionMatchesRequest(string installed, string request)
    {
        if (string.Equals(installed, request, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!request.Contains('.'))
        {
            return installed.StartsWith($"{request}.", StringComparison.OrdinalIgnoreCase);
        }

        if (request.Count(c => c == '.') == 1)
        {
            return installed.StartsWith($"{request}.", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private void PinVersion(string normalized)
    {
        var settings = _settings.Load();
        var pinned = settings.Node.PinnedVersions?.ToList() ?? [];
        if (pinned.Any(version => string.Equals(version, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        pinned.Add(normalized);
        _settings.UpdateNode(settings.Node with { PinnedVersions = pinned });
    }

    private void SyncActiveNodeSetting(string? activeVersion)
    {
        var settings = _settings.Load();
        if (string.Equals(settings.Node.ActiveVersion, activeVersion, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _settings.UpdateNode(settings.Node with { ActiveVersion = activeVersion });
    }

    private IReadOnlyList<string> MergeInstalledVersions(IReadOnlyList<string> listedVersions)
    {
        var versions = new HashSet<string>(listedVersions, StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(NodePaths.VersionsRoot(_paths)))
        {
            return versions.OrderByDescending(static v => v, StringComparer.OrdinalIgnoreCase).ToList();
        }

        foreach (var directory in Directory.EnumerateDirectories(NodePaths.VersionsRoot(_paths), "v*"))
        {
            var name = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(name) || name.Length < 2)
            {
                continue;
            }

            var version = name[1..];
            if (VersionRegex.IsMatch(version) && File.Exists(Path.Combine(directory, "node.exe")))
            {
                versions.Add(version);
            }
        }

        return versions
            .OrderByDescending(static v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<string>> ListVersionsFromNvmAsync(string nvmExe, CancellationToken cancellationToken)
    {
        var listResult = await RunNvmAsync(nvmExe, "list", cancellationToken);
        return ParseNvmList(listResult.Output);
    }

    private static IReadOnlyList<string> ParseNvmList(string output)
    {
        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = Regex.Match(line, @"^\s*(\*?\s*)?(v)?(\d+\.\d+\.\d+)\s*", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                versions.Add(match.Groups[3].Value);
            }
        }

        return versions
            .OrderByDescending(static v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void EnsureNvmConfigured(string nvmExe)
    {
        var nvmHome = Path.GetDirectoryName(nvmExe)
            ?? throw new InvalidOperationException("Invalid nvm.exe path.");
        NvmConfiguration.WriteSettingsFile(nvmHome, _paths);
        NvmConfiguration.PrepareSymlinkDirectory(_paths);
    }

    public async Task<IReadOnlyList<string>> ListInstalledVersionsAsync(CancellationToken cancellationToken = default)
    {
        var nvmPackage = GetInstalledNvmPackage();
        if (nvmPackage is null) return [];

        var nvmExe = ResolveNvmExecutable(nvmPackage.InstallPath);
        if (nvmExe is null) return [];

        return await ListVersionsFromNvmAsync(nvmExe, cancellationToken);
    }

    public void RepairNvmConfiguration()
    {
        var nvmPackage = GetInstalledNvmPackage();
        if (nvmPackage is null)
        {
            return;
        }

        var nvmExe = ResolveNvmExecutable(nvmPackage.InstallPath);
        if (nvmExe is null)
        {
            return;
        }

        EnsureNvmConfigured(nvmExe);
    }

    public async Task RepairNodeRuntimeAsync(CancellationToken cancellationToken = default)
    {
        RepairNvmConfiguration();

        var nvmPackage = GetInstalledNvmPackage();
        if (nvmPackage is null)
        {
            return;
        }

        var nvmExe = ResolveNvmExecutable(nvmPackage.InstallPath);
        if (nvmExe is null)
        {
            return;
        }

        var symlinkNode = Path.Combine(NodePaths.SymlinkPath(_paths), "node.exe");
        if (File.Exists(symlinkNode))
        {
            return;
        }

        var settings = _settings.Load();
        var targetVersion = settings.Node.ActiveVersion;
        if (string.IsNullOrWhiteSpace(targetVersion))
        {
            targetVersion = MergeInstalledVersions(await ListVersionsFromNvmAsync(nvmExe, cancellationToken))
                .FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(targetVersion))
        {
            return;
        }

        try
        {
            await UseVersionAsync(targetVersion, cancellationToken);
        }
        catch
        {
            // Best-effort repair — UI can still activate manually.
        }
    }

    private static string? ResolveNvmExecutable(string installPath)
    {
        var candidates = new[]
        {
            Path.Combine(installPath, "nvm.exe"),
            Path.Combine(installPath, "nvm", "nvm.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private string? ResolveNodeExecutablePath(string? activeVersion = null)
    {
        var symlinkNode = Path.Combine(NodePaths.SymlinkPath(_paths), "node.exe");
        if (File.Exists(symlinkNode))
        {
            return symlinkNode;
        }

        activeVersion ??= _settings.Load().Node.ActiveVersion;
        if (!string.IsNullOrWhiteSpace(activeVersion))
        {
            var direct = Path.Combine(NodePaths.VersionDirectory(_paths, activeVersion), "node.exe");
            if (File.Exists(direct))
            {
                return direct;
            }
        }

        return Directory.Exists(NodePaths.VersionsRoot(_paths))
            ? Directory.EnumerateDirectories(NodePaths.VersionsRoot(_paths), "v*")
                .Select(directory => Path.Combine(directory, "node.exe"))
                .FirstOrDefault(File.Exists)
            : null;
    }

    private async Task<string?> ResolveActiveNodeVersionAsync(CancellationToken cancellationToken)
    {
        var nodeExe = ResolveNodeExecutablePath();
        if (nodeExe is null)
        {
            return null;
        }

        var result = await RunProcessAsync(nodeExe, "-v", Path.GetDirectoryName(nodeExe)!, cancellationToken);
        if (result.ExitCode != 0)
        {
            return null;
        }

        var raw = result.Output.Trim();
        if (raw.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            raw = raw[1..];
        }

        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    private async Task<ExecResult> RunNvmAsync(string nvmExe, string args, CancellationToken cancellationToken)
    {
        var nvmHome = Path.GetDirectoryName(nvmExe) ?? _paths.RuntimeRoot;
        return await RunProcessAsync(nvmExe, args, nvmHome, cancellationToken, BuildNvmEnvironment(nvmExe));
    }

    private Dictionary<string, string> BuildNvmEnvironment(string nvmExe)
    {
        var nvmHome = Path.GetDirectoryName(nvmExe) ?? _paths.RuntimeRoot;
        var symlink = NodePaths.SymlinkPath(_paths);
        var binDir = Path.Combine(_paths.RuntimeRoot, "bin");

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                env[key] = value;
            }
        }

        env["NVM_HOME"] = nvmHome;
        env["NVM_SYMLINK"] = symlink;

        var prepend = new List<string>();
        if (Directory.Exists(binDir))
        {
            prepend.Add(binDir);
        }

        if (Directory.Exists(symlink))
        {
            prepend.Add(symlink);
        }

        prepend.Add(nvmHome);

        var basePath = env.TryGetValue("PATH", out var currentPath) ? currentPath : string.Empty;
        env["PATH"] = prepend.Count == 0
            ? basePath
            : string.Join(';', prepend.Concat(basePath.Split(';', StringSplitOptions.RemoveEmptyEntries)));

        var registry = _settings.Load().Node.NpmRegistry?.Trim();
        if (!string.IsNullOrWhiteSpace(registry))
        {
            env["npm_config_registry"] = registry;
        }

        return env;
    }

    private static async Task<ExecResult> RunProcessAsync(
        string fileName,
        string args,
        string workingDirectory,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = args,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (environment is not null)
        {
            psi.Environment.Clear();
            foreach (var pair in environment)
            {
                psi.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = new Process { StartInfo = psi };
        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var output = (await outputTask) + Environment.NewLine + (await errorTask);
        return new ExecResult(process.ExitCode, output.Trim());
    }

    private static bool LooksLikeNvmSuccess(string output)
        => Regex.IsMatch(output, @"already|now using|installation complete|complete", RegexOptions.IgnoreCase);

    private static string? FirstVersionOrRaw(string output)
    {
        var match = VersionRegex.Match(output);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        var trimmed = output.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private sealed record ExecResult(int ExitCode, string Output);
}
