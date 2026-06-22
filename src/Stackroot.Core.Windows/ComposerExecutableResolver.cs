using System.Diagnostics;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;

namespace Stackroot.Core.Windows;

public static class ComposerExecutableResolver
{
    public sealed record ComposerInvocation(string FileName, IReadOnlyList<string> PrefixArguments);

    public static ComposerInvocation? Resolve(InstallRegistryStore registry, string? phpExe = null)
    {
        var phar = ResolveStackrootPhar(registry);
        if (phar is not null && !string.IsNullOrWhiteSpace(phpExe))
        {
            return new ComposerInvocation(phpExe, [phar]);
        }

        var system = ResolveSystemExecutable();
        return system is null ? null : new ComposerInvocation(system, []);
    }

    public static string? ResolveStackrootPhar(InstallRegistryStore registry)
    {
        var composer = registry.List(PackageType.Composer)
            .OrderByDescending(p => p.Version, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (composer is null)
        {
            return null;
        }

        var candidates = new List<string>
        {
            Path.Combine(composer.InstallPath, "composer.phar"),
            Path.Combine(composer.InstallPath, "bin", "composer.phar")
        };

        if (Directory.Exists(composer.InstallPath))
        {
            candidates.AddRange(Directory.EnumerateFiles(composer.InstallPath, "composer*.phar", SearchOption.TopDirectoryOnly));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    public static string? ResolveSystemExecutable()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        var candidates = new[]
        {
            Path.Combine(appData, "Stackroot", "bin", "composer.cmd"),
            Path.Combine(appData, "Composer", "vendor", "bin", "composer.bat"),
            Path.Combine(localAppData, "Programs", "Composer", "composer.bat"),
            Path.Combine(programFiles, "ComposerSetup", "bin", "composer.bat")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return TryFindOnPath("composer.bat")
            ?? TryFindOnPath("composer.cmd")
            ?? TryFindOnPath("composer.exe")
            ?? TryFindOnPath("composer");
    }

    private static string? TryFindOnPath(string executable)
    {
        try
        {
            var startInfo = ProcessStreamEncoding.CreateStdoutOnly("where.exe");
            startInfo.Arguments = executable;
            using var process = Process.Start(startInfo);

            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return null;
            }

            var first = output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line) && File.Exists(line));

            return first;
        }
        catch
        {
            return null;
        }
    }
}
