using System.Diagnostics;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Node;

namespace Stackroot.Core.Windows;

public static class SiteProcessEnvironment
{
    private static readonly string[] CiEnvironmentKeys =
    [
        "CI",
        "CONTINUOUS_INTEGRATION",
        "GITHUB_ACTIONS",
        "TF_BUILD",
        "GITLAB_CI",
        "BUILD_BUILDID"
    ];

    public static Dictionary<string, string> Build(
        StackrootPaths paths,
        string? phpVersionId,
        INpmTooling? npmTooling = null,
        string? phpBinDirectory = null)
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

        var prepend = new List<string>();
        if (!string.IsNullOrWhiteSpace(phpBinDirectory) && Directory.Exists(phpBinDirectory))
        {
            prepend.Add(phpBinDirectory);
        }

        prepend.AddRange(new[]
        {
            Path.Combine(paths.RuntimeRoot, "bin"),
            NodePaths.SymlinkPath(paths)
        }.Where(Directory.Exists));

        var currentPath = env.TryGetValue("PATH", out var pathValue) ? pathValue : string.Empty;
        env["PATH"] = prepend.Count == 0
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

        env["PYTHONUNBUFFERED"] = "1";
        if (!env.ContainsKey("PHP_WINDOWS_UTF8"))
        {
            env["PHP_WINDOWS_UTF8"] = "1";
        }

        foreach (var key in CiEnvironmentKeys)
        {
            env.Remove(key);
        }

        return env;
    }

    /// <summary>
    /// Pipe-only fallback when ConPTY is unavailable or STACKROOT_USE_PIPES=1.
    /// </summary>
    public static void ApplyRedirectCaptureDefaults(ProcessStartInfo startInfo)
    {
        StripCiEnvironment(startInfo.Environment);
    }

    public static void StripCiEnvironment(IDictionary<string, string?> env)
    {
        foreach (var key in CiEnvironmentKeys)
        {
            env.Remove(key);
        }
    }
}
