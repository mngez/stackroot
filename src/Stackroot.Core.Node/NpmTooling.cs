using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;

namespace Stackroot.Core.Node;

public sealed class NpmTooling : INpmTooling
{
    private readonly StackrootPaths _paths;
    private readonly InstallRegistryStore _registry;

    public NpmTooling(StackrootPaths paths, InstallRegistryStore registry)
    {
        _paths = paths;
        _registry = registry;
    }

    public string? ResolveNpmCommand()
    {
        var candidates = new[]
        {
            Path.Combine(NodePaths.SymlinkPath(_paths), "npm.cmd"),
            Path.Combine(NodePaths.SymlinkPath(_paths), "npm")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    public IReadOnlyDictionary<string, string> BuildCommandEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                env[key] = value;
            }
        }

        var nvmPackage = _registry.List(PackageType.Nvm).FirstOrDefault();
        var nvmHome = nvmPackage?.InstallPath is not null && File.Exists(Path.Combine(nvmPackage.InstallPath, "nvm.exe"))
            ? nvmPackage.InstallPath
            : null;
        var symlink = NodePaths.SymlinkPath(_paths);
        var binDir = Path.Combine(_paths.RuntimeRoot, "bin");

        if (!string.IsNullOrWhiteSpace(nvmHome))
        {
            env["NVM_HOME"] = nvmHome;
        }

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

        if (!string.IsNullOrWhiteSpace(nvmHome))
        {
            prepend.Add(nvmHome);
        }

        var basePath = env.TryGetValue("PATH", out var currentPath) ? currentPath : string.Empty;
        env["PATH"] = prepend.Count == 0
            ? basePath
            : string.Join(';', prepend.Concat(basePath.Split(';', StringSplitOptions.RemoveEmptyEntries)));

        return env;
    }
}
