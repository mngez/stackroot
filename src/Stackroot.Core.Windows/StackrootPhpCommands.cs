using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;

namespace Stackroot.Core.Windows;

public static class StackrootPhpCommands
{
    public static string ResolvePhpExecutable(
        InstallRegistryStore registry,
        string versionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);

        var package = registry.GetById(versionId)
            ?? throw new InvalidOperationException(
                $"PHP {versionId} is not installed. Go to PHP page → select '{versionId}' → Install.");

        var phpExe = PackageBinaryResolver.ResolvePackageBinary(package.InstallPath, "php.exe");
        if (phpExe is null)
        {
            throw new InvalidOperationException(
                $"php.exe is missing for {versionId}. Try reinstalling from the PHP page: select '{versionId}' → Uninstall, then Install again.");
        }

        return phpExe;
    }

    public static string? ResolvePhpAlias(InstallRegistryStore registry, string versionId) =>
        PhpRuntimeAliases.AliasForPackageId(versionId);

    public static List<string> BuildArtisanArguments(string workingDirectory, IReadOnlyList<string> argv)
    {
        var usesArtisan = argv.Any(part => string.Equals(part, "artisan", StringComparison.OrdinalIgnoreCase));
        if (!usesArtisan)
        {
            return argv.ToList();
        }

        var artisanPath = Path.Combine(workingDirectory, "artisan");
        if (!File.Exists(artisanPath))
        {
            throw new FileNotFoundException(
                "Laravel artisan file was not found in the project folder.",
                artisanPath);
        }

        return argv.ToList();
    }

    public static string FormatDisplayCommand(string? alias, IReadOnlyList<string> argv)
    {
        var command = string.Join(' ', argv);
        return string.IsNullOrWhiteSpace(alias) ? command : $"{alias} {command}";
    }
}
