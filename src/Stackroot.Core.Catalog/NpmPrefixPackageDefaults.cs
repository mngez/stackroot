using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Catalog;

public static class NpmPrefixPackageDefaults
{
    public const string DefaultPnpmVersion = "10.12.4";
    public const string DefaultPnpmPackageId = "pnpm-10.12.4";

    public static PackageEntry DefaultPnpmEntry() =>
        new()
        {
            Id = DefaultPnpmPackageId,
            Type = PackageType.Pnpm,
            Version = DefaultPnpmVersion,
            Label = $"pnpm {DefaultPnpmVersion}",
            Description = "Fast Node package manager — installed via npm into Stackroot",
            InstallDir = $"tools/pnpm/{DefaultPnpmVersion}"
        };

    public static string ExpectedCmdName(PackageType type) =>
        type switch
        {
            PackageType.Pnpm => "pnpm.cmd",
            PackageType.Vite => "vite.cmd",
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not an npm-prefix package.")
        };

    public static bool IsInstallHealthy(InstalledPackage package)
    {
        if (!Directory.Exists(package.InstallPath))
        {
            return false;
        }

        var cmdName = ExpectedCmdName(package.Type);
        return File.Exists(Path.Combine(package.InstallPath, "node_modules", ".bin", cmdName));
    }

    public static PackageEntry? FallbackEntryFor(InstalledPackage package) =>
        package.Type switch
        {
            PackageType.Pnpm when string.Equals(package.Id, DefaultPnpmPackageId, StringComparison.OrdinalIgnoreCase)
                => DefaultPnpmEntry(),
            PackageType.Pnpm => new PackageEntry
            {
                Id = package.Id,
                Type = PackageType.Pnpm,
                Version = package.Version,
                Label = $"pnpm {package.Version}",
                InstallDir = $"tools/pnpm/{package.Version}"
            },
            PackageType.Vite => new PackageEntry
            {
                Id = package.Id,
                Type = PackageType.Vite,
                Version = package.Version,
                Label = $"vite {package.Version}",
                InstallDir = $"tools/vite/{package.Version}"
            },
            _ => null
        };
}
