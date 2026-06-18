using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Node;

namespace Stackroot.Core.Windows;

public static class SiteProcessEnvironment
{
    public static Dictionary<string, string> Build(
        StackrootPaths paths,
        string? phpVersionId,
        INpmTooling? npmTooling = null)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                env[key] = value;
            }
        }

        if (npmTooling is not null)
        {
            foreach (var pair in npmTooling.BuildCommandEnvironment())
            {
                env[pair.Key] = pair.Value;
            }
        }

        var prepend = new[]
        {
            Path.Combine(paths.RuntimeRoot, "bin"),
            NodePaths.SymlinkPath(paths)
        }
        .Where(Directory.Exists)
        .ToArray();

        var currentPath = env.TryGetValue("PATH", out var pathValue) ? pathValue : string.Empty;
        env["PATH"] = prepend.Length == 0
            ? currentPath
            : string.Join(Path.PathSeparator, prepend) + Path.PathSeparator + currentPath;

        if (!string.IsNullOrWhiteSpace(phpVersionId))
        {
            var iniPath = PhpConfigPaths.ResolveExistingDefaultIniPath(paths.ConfigRoot, phpVersionId);
            if (!string.IsNullOrWhiteSpace(iniPath))
            {
                env["PHPRC"] = iniPath;
            }
        }

        env["CI"] = env.TryGetValue("CI", out var ci) ? ci : "1";
        env["TERM"] = env.TryGetValue("TERM", out var term) ? term : "dumb";
        env["PYTHONUNBUFFERED"] = "1";

        return env;
    }
}
