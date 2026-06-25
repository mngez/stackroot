using System.Diagnostics;
using System.Text;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Node;
using Stackroot.Core.Catalog;
using Stackroot.Core.Settings;

namespace Stackroot.Core.Windows;

public sealed class StackrootBinManager
{
    private readonly StackrootPaths _paths;
    private readonly InstallRegistryStore _registry;
    private readonly SettingsStore _settings;

    public StackrootBinManager(StackrootPaths paths, InstallRegistryStore registry, SettingsStore settings)
    {
        _paths = paths;
        _registry = registry;
        _settings = settings;
    }

    public string BinDirectory => Path.Combine(_paths.RuntimeRoot, "bin");

    public string LegacyBinDirectory => Path.Combine(_paths.DataRoot, "bin");

    private HashSet<string>? _activeShims;

    public Task SyncStackrootBinAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = _settings.Load();

        Directory.CreateDirectory(BinDirectory);
        _activeShims = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        SyncPhpShims(settings);
        SyncComposerShim();
        SyncNodeShims();
        SyncNvmShim();
        SyncServiceShims(settings);
        SyncToolShims();

        ClearStaleShims();
        _activeShims = null;

        RemoveLegacyBinFromUserPath();

        if (settings.General.AddBinToPath == true)
        {
            EnsureUserPathContainsBin();
        }
        else
        {
            RemoveBinFromUserPath();
        }

        return Task.CompletedTask;
    }

    private void ClearStaleShims()
    {
        if (!Directory.Exists(BinDirectory) || _activeShims is null)
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(BinDirectory, "*.cmd")
            .Concat(Directory.EnumerateFiles(BinDirectory, "*.bat")))
        {
            if (!_activeShims.Contains(file))
            {
                File.Delete(file);
            }
        }
    }

    private void SyncPhpShims(AppSettings settings)
    {
        var phpPackages = _registry.List(PackageType.Php);
        foreach (var package in phpPackages)
        {
            var phpExe = PackageBinaryResolver.ResolvePackageBinary(package.InstallPath, "php.exe");
            if (phpExe is null)
            {
                continue;
            }

            var alias = PhpRuntimeAliases.AliasForPackageId(package.Id);
            if (!string.IsNullOrWhiteSpace(alias))
            {
                WriteShim($"{alias}.cmd", WrapExecutable(phpExe));
            }
        }

        var activeVersionId = settings.Php.ActiveVersionId ?? phpPackages.FirstOrDefault()?.Id;
        if (string.IsNullOrWhiteSpace(activeVersionId))
        {
            return;
        }

        var activePackage = _registry.GetById(activeVersionId);
        if (activePackage is null)
        {
            return;
        }

        var activePhpExe = PackageBinaryResolver.ResolvePackageBinary(activePackage.InstallPath, "php.exe");
        if (activePhpExe is not null)
        {
            WriteShim("php.cmd", WrapExecutable(activePhpExe));
        }
    }

    private void SyncComposerShim()
    {
        var composer = _registry.List(PackageType.Composer)
            .OrderByDescending(package => package.Version, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (composer is null)
        {
            return;
        }

        var composerBat = Path.Combine(composer.InstallPath, "composer.bat");
        if (File.Exists(composerBat))
        {
            WriteShim("composer.cmd", WrapExecutable(composerBat));
            return;
        }

        var composerPhar = ComposerPharResolver.EnsureStandardPharPath(composer.InstallPath)
            ?? ComposerPharResolver.FindPharPath(composer.InstallPath);

        if (!File.Exists(composerPhar))
        {
            return;
        }

        WriteShim("composer.cmd", $"""
@echo off
setlocal
"%~dp0php.cmd" "{EscapeForCmd(composerPhar)}" %*
exit /b %ERRORLEVEL%
""");
    }

    private void SyncNodeShims()
    {
        var nodeExe = Path.Combine(NodePaths.SymlinkPath(_paths), "node.exe");
        var npmCmd = Path.Combine(NodePaths.SymlinkPath(_paths), "npm.cmd");
        var npxCmd = Path.Combine(NodePaths.SymlinkPath(_paths), "npx.cmd");
        var corepackCmd = Path.Combine(NodePaths.SymlinkPath(_paths), "corepack.cmd");

        if (!File.Exists(nodeExe))
        {
            return;
        }

        WriteShim("node.cmd", WrapExecutable(nodeExe));

        if (File.Exists(npmCmd))
        {
            WriteShim("npm.cmd", $"""
@echo off
call "{EscapeForCmd(npmCmd)}" %*
exit /b %ERRORLEVEL%
""");
        }

        if (File.Exists(npxCmd))
        {
            WriteShim("npx.cmd", $"""
@echo off
call "{EscapeForCmd(npxCmd)}" %*
exit /b %ERRORLEVEL%
""");
        }

        if (File.Exists(corepackCmd))
        {
            WriteShim("corepack.cmd", $"""
@echo off
call "{EscapeForCmd(corepackCmd)}" %*
exit /b %ERRORLEVEL%
""");
        }
    }

    private void SyncNvmShim()
    {
        var nvmPackage = _registry.List(PackageType.Nvm)
            .OrderByDescending(package => package.Version, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (nvmPackage is null)
        {
            return;
        }

        var nvmExe = ResolveNvmExecutable(nvmPackage.InstallPath);
        if (nvmExe is null)
        {
            return;
        }

        var nvmHome = nvmPackage.InstallPath;
        var nvmSymlink = NodePaths.SymlinkPath(_paths);
        WriteShim("nvm.cmd", $"""
@echo off
set "NVM_HOME={EscapeForCmd(nvmHome)}"
set "NVM_SYMLINK={EscapeForCmd(nvmSymlink)}"
"{EscapeForCmd(nvmExe)}" %*
exit /b %ERRORLEVEL%
""");
    }

    private void SyncServiceShims(AppSettings settings)
    {
        WriteServiceShim("nginx.cmd", ServiceId.Nginx, "nginx.exe");
        WriteServiceShim("redis-cli.cmd", ServiceId.Redis, "redis-cli.exe");
        WriteServiceShim("magick.cmd", ServiceId.Imagemagick, "magick.exe");
        WriteServiceShim("psql.cmd", ServiceId.Postgresql, "bin/psql.exe");

        var sqlEngine = settings.Databases.ActiveSqlEngine ?? SqlEngine.Mysql;
        if (sqlEngine == SqlEngine.Mysql)
        {
            WriteServiceShim("mysql.cmd", ServiceId.Mysql, "bin/mysql.exe");
            WriteServiceShim("mysqldump.cmd", ServiceId.Mysql, "bin/mysqldump.exe");
        }
        else if (sqlEngine == SqlEngine.Mariadb)
        {
            WriteServiceShim("mysql.cmd", ServiceId.Mariadb, "bin/mysql.exe");
            WriteServiceShim("mysqldump.cmd", ServiceId.Mariadb, "bin/mysqldump.exe");
        }
    }

    private void SyncToolShims()
    {
        WritePackageShim("git.cmd", PackageType.Git, "cmd/git.exe", "bin/git.exe", "git.exe");
        SyncPnpmShims();
        SyncViteShim();
        WritePackageShim("python.cmd", PackageType.Python, "python.exe");
        WritePackageShim("sqlite3.cmd", PackageType.Sqlite, "sqlite3.exe");
        WritePackageShim("laravel.cmd", PackageType.Laravel, "bin/laravel.bat", "laravel.bat", "laravel");
        SyncWpCliShim();
        WritePackageShim("mongosh.cmd", PackageType.Mongosh, "bin/mongosh.exe");
        WritePackageShim("mongodump.cmd", PackageType.MongodbTools, "bin/mongodump.exe");
        WritePackageShim("mongorestore.cmd", PackageType.MongodbTools, "bin/mongorestore.exe");
    }

    private void SyncPnpmShims()
    {
        var pnpmBin = ResolveInstalledPnpmBin();
        if (pnpmBin is not { Pnpm: { Length: > 0 } pnpm })
        {
            return;
        }

        WriteShim("pnpm.cmd", WrapExecutable(pnpm));
        if (!string.IsNullOrWhiteSpace(pnpmBin.Value.Pnpx))
        {
            WriteShim("pnpx.cmd", WrapExecutable(pnpmBin.Value.Pnpx));
        }
    }

    private void SyncViteShim()
    {
        var viteCmd = ResolveInstalledViteBin();
        if (viteCmd is null)
        {
            return;
        }

        WriteShim("vite.cmd", WrapExecutable(viteCmd));
    }

    private void SyncWpCliShim()
    {
        var installed = _registry.List(PackageType.WpCli)
            .OrderByDescending(p => p.Version, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (installed is null) return;

        var phar = Path.Combine(installed.InstallPath, "wp-cli.phar");
        if (!File.Exists(phar)) return;

        WriteShim("wp.bat", $"""
@echo off
setlocal
"%~dp0php.cmd" "{EscapeForCmd(phar)}" %*
exit /b %ERRORLEVEL%
""");
    }

    private (string Pnpm, string? Pnpx)? ResolveInstalledPnpmBin()
    {
        var installed = _registry.List(PackageType.Pnpm)
            .OrderByDescending(package => package.Version, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (installed is null)
        {
            return null;
        }

        var pnpm = Path.Combine(installed.InstallPath, "node_modules", ".bin", "pnpm.cmd");
        if (!File.Exists(pnpm))
        {
            pnpm = PackageBinaryResolver.ResolvePackageBinary(installed.InstallPath, "pnpm.cmd")
                ?? PackageBinaryResolver.ResolvePackageBinary(installed.InstallPath, "pnpm.exe")
                ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(pnpm) || !File.Exists(pnpm))
        {
            return null;
        }

        var pnpx = Path.Combine(installed.InstallPath, "node_modules", ".bin", "pnpx.cmd");
        if (!File.Exists(pnpx))
        {
            pnpx = PackageBinaryResolver.ResolvePackageBinary(installed.InstallPath, "pnpx.cmd")
                ?? PackageBinaryResolver.ResolvePackageBinary(installed.InstallPath, "pnpx.exe");
        }

        return (pnpm, pnpx is not null && File.Exists(pnpx) ? pnpx : null);
    }

    private string? ResolveInstalledViteBin()
    {
        var installed = _registry.List(PackageType.Vite)
            .OrderByDescending(package => package.Version, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (installed is not null)
        {
            var registeredCmd = Path.Combine(installed.InstallPath, "node_modules", ".bin", "vite.cmd");
            if (File.Exists(registeredCmd))
            {
                return registeredCmd;
            }

            var resolved = PackageBinaryResolver.ResolvePackageBinary(installed.InstallPath, "vite.cmd")
                ?? PackageBinaryResolver.ResolvePackageBinary(installed.InstallPath, "vite.exe");
            if (resolved is not null)
            {
                return resolved;
            }
        }

        var toolsRoot = Path.Combine(_paths.RuntimeRoot, "tools", "vite");
        if (!Directory.Exists(toolsRoot))
        {
            return null;
        }

        return Directory.EnumerateDirectories(toolsRoot)
            .Select(directory => Path.Combine(directory, "node_modules", ".bin", "vite.cmd"))
            .Where(File.Exists)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .LastOrDefault();
    }

    private void WriteServiceShim(string shimName, ServiceId serviceId, string relativeExecutable)
    {
        var settings = _settings.Load();
        if (!settings.Services.TryGetValue(serviceId, out var serviceSettings) || !serviceSettings.Enabled)
        {
            return;
        }

        var packageId = serviceSettings.PackageId
            ?? SettingsDefaults.ServiceDefinitions.First(definition => definition.Id == serviceId).PackageId;
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return;
        }

        var installed = _registry.GetById(packageId);
        if (installed is null)
        {
            return;
        }

        var executable = PackageBinaryResolver.ResolvePackageBinary(installed.InstallPath, relativeExecutable);
        if (executable is null)
        {
            return;
        }

        WriteShim(shimName, WrapExecutable(executable));
    }

    private void WritePackageShim(string shimName, PackageType packageType, params string[] relativeCandidates)
    {
        var installed = _registry.List(packageType)
            .OrderByDescending(package => package.Version, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (installed is null)
        {
            return;
        }

        string? executable = null;
        foreach (var candidate in relativeCandidates)
        {
            executable = PackageBinaryResolver.ResolvePackageBinary(installed.InstallPath, candidate);
            if (executable is not null)
            {
                break;
            }
        }

        if (executable is null)
        {
            return;
        }

        WriteShim(shimName, WrapExecutable(executable));
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

    private static string WrapExecutable(string executablePath)
    {
        return $"""
@echo off
"{EscapeForCmd(executablePath)}" %*
exit /b %ERRORLEVEL%
""";
    }

    private void EnsureUserPathContainsBin()
    {
        var userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? string.Empty;
        var alreadyPresent = userPath
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(path => string.Equals(path.TrimEnd('\\'), BinDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

        if (alreadyPresent)
        {
            return;
        }

        var updatedPath = string.IsNullOrWhiteSpace(userPath) ? BinDirectory : $"{userPath};{BinDirectory}";
        SetUserPath(updatedPath);
    }

    private void RemoveBinFromUserPath()
    {
        RemovePathEntry(BinDirectory);
    }

    private void RemoveLegacyBinFromUserPath()
    {
        RemovePathEntry(LegacyBinDirectory);
    }

    private void RemovePathEntry(string directory)
    {
        var userPath = Environment.GetEnvironmentVariable("Path", EnvironmentVariableTarget.User) ?? string.Empty;
        var entries = userPath
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(path => !string.Equals(path.TrimEnd('\\'), directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (entries.Count == userPath.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length)
        {
            return;
        }

        SetUserPath(string.Join(';', entries));
    }

    private static void SetUserPath(string pathValue)
    {
        var escaped = pathValue.Replace("'", "''");
        var script = $"[Environment]::SetEnvironmentVariable('Path', '{escaped}', 'User')";
        var psi = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{script}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        process?.WaitForExit(5000);
    }

    private void WriteShim(string fileName, string content)
    {
        var path = Path.Combine(BinDirectory, fileName);
        _activeShims?.Add(path);

        var normalizedContent = content.Replace("\n", Environment.NewLine);
        if (File.Exists(path) && File.ReadAllText(path, new UTF8Encoding(false)) == normalizedContent)
        {
            return;
        }

        File.WriteAllText(path, normalizedContent, new UTF8Encoding(false));
    }

    private static string EscapeForCmd(string path)
    {
        return path.Replace("\"", "\"\"");
    }
}
