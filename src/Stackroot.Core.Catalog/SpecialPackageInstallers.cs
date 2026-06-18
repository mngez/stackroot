using System.Diagnostics;
using System.Net.Http;
using System.Text;
using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Catalog;

public sealed class ComposerInstaller
{
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Stackroot/0.1";

    private readonly HttpClient _httpClient;
    private readonly InstallRegistryStore _registry;

    public ComposerInstaller(string dataRoot, HttpClient? httpClient = null)
    {
        _registry = new InstallRegistryStore(dataRoot);
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    }

    public async Task<string> InstallAsync(
        PackageEntry entry,
        string runtimeRoot,
        string phpExePath,
        InstallProgressCallback? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var url = entry.Remote?.Url
            ?? (entry.Source.Type == PackageSourceType.Remote ? entry.Source.Url : null);
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("Composer package has no download URL.");
        }

        if (!File.Exists(phpExePath))
        {
            throw new InvalidOperationException($"php.exe not found: {phpExePath}");
        }

        var installPath = CatalogPaths.InstallDirForPackage(runtimeRoot, entry.InstallDir);
        Directory.CreateDirectory(installPath);

        var pharPath = Path.Combine(installPath, "composer.phar");
        var batPath = Path.Combine(installPath, "composer.bat");

        onProgress?.Invoke(new InstallProgress
        {
            PackageId = entry.Id,
            Phase = InstallPhase.Downloading,
            Percent = 20,
            Message = "Downloading Composer..."
        });

        await DownloadFileAsync(url, pharPath, cancellationToken);
        ComposerPharResolver.WriteComposerBat(batPath, phpExePath, pharPath);
        Register(entry, installPath, PackageSourceType.Remote);

        onProgress?.Invoke(new InstallProgress
        {
            PackageId = entry.Id,
            Phase = InstallPhase.Done,
            Percent = 100,
            Message = "Composer ready."
        });

        return installPath;
    }

    public static void RepairInstallation(string installPath, string phpExePath)
    {
        if (!File.Exists(phpExePath))
        {
            return;
        }

        var pharPath = ComposerPharResolver.EnsureStandardPharPath(installPath);
        if (pharPath is null)
        {
            return;
        }

        ComposerPharResolver.WriteComposerBat(Path.Combine(installPath, "composer.bat"), phpExePath, pharPath);
    }

    private void Register(PackageEntry entry, string installPath, PackageSourceType source)
    {
        _registry.Register(new InstalledPackage
        {
            Id = entry.Id,
            Type = entry.Type,
            Version = entry.Version,
            InstalledAt = DateTimeOffset.UtcNow.ToString("O"),
            InstallPath = installPath,
            Source = source
        });
    }

    private async Task DownloadFileAsync(string url, string destination, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var file = File.Create(destination);
        await stream.CopyToAsync(file, cancellationToken);
    }
}

public sealed class NpmPrefixPackageInstaller
{
    private readonly InstallRegistryStore _registry;
    private readonly INpmTooling _npmTooling;

    public NpmPrefixPackageInstaller(string dataRoot, INpmTooling npmTooling)
    {
        _registry = new InstallRegistryStore(dataRoot);
        _npmTooling = npmTooling;
    }

    public Task<string> InstallPnpmAsync(
        PackageEntry entry,
        string runtimeRoot,
        InstallProgressCallback? onProgress = null,
        CancellationToken cancellationToken = default)
        => InstallAsync(entry, runtimeRoot, $"pnpm@{entry.Version}", "pnpm.cmd", onProgress, cancellationToken);

    public Task<string> InstallViteAsync(
        PackageEntry entry,
        string runtimeRoot,
        InstallProgressCallback? onProgress = null,
        CancellationToken cancellationToken = default)
        => InstallAsync(entry, runtimeRoot, $"vite@{entry.Version}", "vite.cmd", onProgress, cancellationToken);

    public async Task<string> InstallAsync(
        PackageEntry entry,
        string runtimeRoot,
        string npmSpecifier,
        string expectedCmdName,
        InstallProgressCallback? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var npm = _npmTooling.ResolveNpmCommand()
            ?? throw new InvalidOperationException("Install nvm-windows and activate a Node version first.");

        var installPath = CatalogPaths.InstallDirForPackage(runtimeRoot, entry.InstallDir);
        PrepareInstallDirectory(installPath);

        onProgress?.Invoke(new InstallProgress
        {
            PackageId = entry.Id,
            Phase = InstallPhase.Downloading,
            Percent = 20,
            Message = $"Installing {entry.Label}..."
        });

        var args =
            $"install {npmSpecifier} --prefix \"{installPath}\" --no-fund --no-audit --loglevel=error";
        var result = await RunNpmAsync(npm, args, installPath, cancellationToken);
        var expectedCmd = Path.Combine(installPath, "node_modules", ".bin", expectedCmdName);
        if (result.ExitCode != 0 || !File.Exists(expectedCmd))
        {
            var detail = string.Join(Environment.NewLine, new[] { result.Output }.Where(static line => !string.IsNullOrWhiteSpace(line)));
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? $"{entry.Label} install failed — check Node and npm."
                : detail);
        }

        _registry.Register(new InstalledPackage
        {
            Id = entry.Id,
            Type = entry.Type,
            Version = entry.Version,
            InstalledAt = DateTimeOffset.UtcNow.ToString("O"),
            InstallPath = installPath,
            Source = PackageSourceType.Remote
        });

        onProgress?.Invoke(new InstallProgress
        {
            PackageId = entry.Id,
            Phase = InstallPhase.Done,
            Percent = 100,
            Message = $"{entry.Label} ready."
        });

        return installPath;
    }

    private static void PrepareInstallDirectory(string installPath)
    {
        if (!Directory.Exists(installPath))
        {
            Directory.CreateDirectory(installPath);
            return;
        }

        foreach (var stale in Directory.EnumerateFiles(installPath, "*.tar"))
        {
            File.Delete(stale);
        }
    }

    private async Task<ProcessResult> RunNpmAsync(
        string npmCommand,
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/d /s /c \"\"{npmCommand}\" {arguments}\"",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var pair in _npmTooling.BuildCommandEnvironment())
        {
            psi.Environment[pair.Key] = pair.Value;
        }

        using var process = new Process { StartInfo = psi };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, (await outputTask) + Environment.NewLine + (await errorTask));
    }

    private sealed record ProcessResult(int ExitCode, string Output);
}

public sealed class LaravelInstaller
{
    public async Task<string> InstallAsync(
        PackageEntry entry,
        string runtimeRoot,
        InstallRegistryStore registry,
        string phpExePath,
        string runtimeBinDirectory,
        InstallProgressCallback? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var composer = registry.List(PackageType.Composer).FirstOrDefault()
            ?? throw new InvalidOperationException("Install Composer from Tools before the Laravel installer.");

        var pharPath = ComposerPharResolver.EnsureStandardPharPath(composer.InstallPath)
            ?? throw new InvalidOperationException($"composer.phar not found in {composer.InstallPath}");

        var installPath = CatalogPaths.InstallDirForPackage(runtimeRoot, entry.InstallDir);
        var binDir = Path.Combine(installPath, "bin");
        var composerHome = Path.Combine(installPath, "composer-home");
        Directory.CreateDirectory(installPath);
        Directory.CreateDirectory(binDir);
        Directory.CreateDirectory(composerHome);

        var installerSpec = string.IsNullOrWhiteSpace(entry.Version)
            ? "laravel/installer"
            : $"laravel/installer:{entry.Version}";

        onProgress?.Invoke(new InstallProgress
        {
            PackageId = entry.Id,
            Phase = InstallPhase.Downloading,
            Percent = 25,
            Message = $"Installing {installerSpec} via Composer..."
        });

        var psi = new ProcessStartInfo
        {
            FileName = phpExePath,
            Arguments =
                $"\"{pharPath}\" global require {installerSpec} --no-interaction --no-ansi --no-progress --with-all-dependencies",
            WorkingDirectory = installPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (System.Collections.DictionaryEntry envEntry in Environment.GetEnvironmentVariables())
        {
            if (envEntry.Key is string key && envEntry.Value is string value)
            {
                psi.Environment[key] = value;
            }
        }

        psi.Environment["COMPOSER_HOME"] = composerHome;
        psi.Environment["COMPOSER_BIN_DIR"] = binDir;
        PrependPath(psi.Environment, runtimeBinDirectory);

        using var process = new Process { StartInfo = psi };
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, new[] { error, output }.Where(static line => !string.IsNullOrWhiteSpace(line))));
        }

        var laravelBat = Path.Combine(binDir, "laravel.bat");
        if (!File.Exists(laravelBat))
        {
            var laravelPhar = Path.Combine(composerHome, "vendor", "bin", "laravel");
            if (!File.Exists(laravelPhar))
            {
                throw new InvalidOperationException("Laravel executable was not created — check Composer output.");
            }

            File.WriteAllText(
                laravelBat,
                $"@echo off\r\n\"{phpExePath.Replace('/', '\\')}\" \"{laravelPhar.Replace('/', '\\')}\" %*\r\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        registry.Register(new InstalledPackage
        {
            Id = entry.Id,
            Type = entry.Type,
            Version = entry.Version,
            InstalledAt = DateTimeOffset.UtcNow.ToString("O"),
            InstallPath = installPath,
            Source = PackageSourceType.Remote
        });

        onProgress?.Invoke(new InstallProgress
        {
            PackageId = entry.Id,
            Phase = InstallPhase.Done,
            Percent = 100,
            Message = "Laravel installer ready."
        });

        return installPath;
    }

    private static void PrependPath(IDictionary<string, string?> environment, string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        environment.TryGetValue("PATH", out var currentPath);
        environment["PATH"] = string.IsNullOrWhiteSpace(currentPath)
            ? directory
            : $"{directory};{currentPath}";
    }
}
