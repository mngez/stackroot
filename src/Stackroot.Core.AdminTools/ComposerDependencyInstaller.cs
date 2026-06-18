using System.Diagnostics;
using System.Text;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Windows;

namespace Stackroot.Core.AdminTools;

internal static class ComposerDependencyInstaller
{
    public static void EnsureVendor(
        InstallRegistryStore registry,
        string projectDirectory,
        string? preferredPhpVersionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);

        var autoloadPath = Path.Combine(projectDirectory, "vendor", "autoload.php");
        if (File.Exists(autoloadPath))
        {
            return;
        }

        if (!File.Exists(Path.Combine(projectDirectory, "composer.json")))
        {
            throw new InvalidOperationException($"composer.json was not found in {projectDirectory}");
        }

        var phpExe = ResolvePhpExe(registry, preferredPhpVersionId)
            ?? throw new InvalidOperationException("PHP is required to install Composer dependencies.");
        var composer = ComposerExecutableResolver.Resolve(registry, phpExe)
            ?? throw new InvalidOperationException(
                "Composer is not available. Install Composer from Tools, or add composer to your system PATH.");

        var startInfo = new ProcessStartInfo
        {
            FileName = composer.FileName,
            WorkingDirectory = projectDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var prefix in composer.PrefixArguments)
        {
            startInfo.ArgumentList.Add(prefix);
        }
        startInfo.ArgumentList.Add("install");
        startInfo.ArgumentList.Add("--no-dev");
        startInfo.ArgumentList.Add("--no-interaction");
        startInfo.ArgumentList.Add("--prefer-dist");
        startInfo.ArgumentList.Add("--no-ansi");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start composer install.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (!File.Exists(autoloadPath))
        {
            var details = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                details.AppendLine(stderr.Trim());
            }

            if (!string.IsNullOrWhiteSpace(stdout))
            {
                details.AppendLine(stdout.Trim());
            }

            var message = details.Length > 0
                ? details.ToString().Trim()
                : $"composer install exited with code {process.ExitCode}.";
            throw new InvalidOperationException(message);
        }
    }

    private static string? ResolvePhpExe(InstallRegistryStore registry, string? preferredPhpVersionId)
    {
        if (!string.IsNullOrWhiteSpace(preferredPhpVersionId))
        {
            var preferred = registry.GetById(preferredPhpVersionId);
            var preferredExe = preferred is null ? null : ResolvePhpExeFromPath(preferred.InstallPath);
            if (preferredExe is not null)
            {
                return preferredExe;
            }
        }

        foreach (var package in registry.List(PackageType.Php).OrderByDescending(p => p.Id, StringComparer.OrdinalIgnoreCase))
        {
            var exe = ResolvePhpExeFromPath(package.InstallPath);
            if (exe is not null)
            {
                return exe;
            }
        }

        return null;
    }

    private static string? ResolvePhpExeFromPath(string installPath)
    {
        var candidates = new[]
        {
            Path.Combine(installPath, "php.exe"),
            Path.Combine(installPath, "bin", "php.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
