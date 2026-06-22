using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text.RegularExpressions;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;

namespace Stackroot.Core.Services.Php;

public delegate void PeclProgressCallback(string message, int percent);

public sealed class PeclInstaller
{
    private const string UserAgent = "Stackroot/0.1 (+https://stackroot.dev)";
    private static readonly Regex CopiedDllRegex = new(@"Copied DLL to:\s*(.+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly StackrootPaths _paths;
    private readonly InstallRegistryStore _registry;
    private readonly PhpExtensionsManifestStore _manifestStore;
    private readonly HttpClient _httpClient;

    public PeclInstaller(
        StackrootPaths paths,
        InstallRegistryStore registry,
        PhpExtensionsManifestStore manifestStore,
        HttpClient? httpClient = null)
    {
        _paths = paths;
        _registry = registry;
        _manifestStore = manifestStore;
        _httpClient = httpClient ?? new HttpClient();
    }

    public string PiePharPath => Path.Combine(_paths.ResourcesRoot, "packages", "pie.phar");

    public bool IsPiePharAvailable => File.Exists(PiePharPath);

    public async Task<string> InstallAsync(
        string extensionId,
        string phpPackageId,
        PeclProgressCallback? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var entry = _manifestStore.GetExtension(extensionId)
                    ?? throw new InvalidOperationException($"Unknown extension: {extensionId}");

        if (entry.WindowsSupported == false)
        {
            throw new InvalidOperationException($"{entry.Label} is not supported on Windows — use WSL or Linux.");
        }

        var installed = _registry.GetById(phpPackageId)
                        ?? throw new InvalidOperationException($"PHP not installed: {phpPackageId}");

        onProgress?.Invoke($"Installing {entry.Label}…", 5);

        var directBuild = _manifestStore.ResolvePeclBuild(extensionId, phpPackageId);
        if (directBuild is not null)
        {
            onProgress?.Invoke($"Downloading {entry.Label} from PECL…", 20);
            return await InstallFromPeclZipAsync(entry, installed.InstallPath, directBuild, onProgress, cancellationToken);
        }

        var pieSpec = _manifestStore.ResolvePieInstallSpec(extensionId, phpPackageId);
        if (pieSpec is not null && IsPiePharAvailable)
        {
            onProgress?.Invoke($"PIE: {pieSpec}", 20);
            return await InstallViaPieAsync(pieSpec, phpPackageId, onProgress, cancellationToken);
        }

        if (pieSpec is not null && !IsPiePharAvailable)
        {
            throw new InvalidOperationException(
                "pie.phar was not found in resources/packages. Copy pie.phar from Stackroot resources, " +
                "or use a PHP version with a direct PECL Windows build in php-extensions.json.");
        }

        throw new InvalidOperationException($"No install method for {entry.Label} on {phpPackageId}.");
    }

    private async Task<string> InstallFromPeclZipAsync(
        PhpExtensionManifestEntry entry,
        string installPath,
        PeclBuildSpec build,
        PeclProgressCallback? onProgress,
        CancellationToken cancellationToken)
    {
        var root = PhpExtensionPolicy.ResolvePackageRoot(installPath);
        var extDir = Path.Combine(root, "ext");
        Directory.CreateDirectory(extDir);

        var cacheDir = Path.Combine(_paths.RuntimeRoot, ".cache", "pecl");
        Directory.CreateDirectory(cacheDir);
        var archivePath = Path.Combine(cacheDir, $"{entry.Id}-{Path.GetFileName(new Uri(build.ArchiveUrl).AbsolutePath)}");

        onProgress?.Invoke($"Downloading {entry.Label}…", 30);
        await DownloadFileAsync(build.ArchiveUrl, archivePath, cancellationToken);

        onProgress?.Invoke($"Extracting {entry.Label}…", 55);
        var extractDir = Path.Combine(cacheDir, $"{entry.Id}-extract");
        if (Directory.Exists(extractDir))
        {
            Directory.Delete(extractDir, recursive: true);
        }

        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(archivePath, extractDir, overwriteFiles: true);

        var dllSource = FindDll(extractDir, build.Dll)
                        ?? throw new InvalidOperationException($"DLL {build.Dll} not found in PECL archive.");

        CopyAllDllsFromExtract(extractDir, extDir, root);
        var dllDest = Path.Combine(extDir, build.Dll);
        if (!File.Exists(dllDest))
        {
            File.Copy(dllSource, dllDest, overwrite: true);
        }

        if (Directory.Exists(extractDir))
        {
            Directory.Delete(extractDir, recursive: true);
        }

        onProgress?.Invoke($"{entry.Label} installed.", 100);
        return dllDest;
    }

    private async Task<string> InstallViaPieAsync(
        string piePackage,
        string phpPackageId,
        PeclProgressCallback? onProgress,
        CancellationToken cancellationToken)
    {
        var runner = FindPieRunner(phpPackageId)
                     ?? throw new InvalidOperationException("PIE requires PHP 8.1 or newer as runner. Install PHP 8.1+ first.");

        var targetInstalled = _registry.GetById(phpPackageId)
                              ?? throw new InvalidOperationException($"PHP not installed: {phpPackageId}");
        var targetPhp = ResolvePhpExecutable(targetInstalled.InstallPath)
                        ?? throw new InvalidOperationException($"php.exe not found for {phpPackageId}");

        var targetPhpRc = EnsurePieTargetIni(targetInstalled.InstallPath, phpPackageId);
        onProgress?.Invoke($"PIE install {piePackage}…", 40);

        var result = await RunPieAsync(
            runner,
            [
                "install",
                piePackage,
                $"--with-php-path={targetPhp}",
                "--skip-enable-extension",
                "--no-ansi",
                "--no-interaction",
                "--no-cache"
            ],
            targetPhpRc,
            cancellationToken);

        var combined = $"{result.Stdout}\n{result.Stderr}".Trim();
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"PIE install failed: {(string.IsNullOrWhiteSpace(combined) ? $"exit code {result.ExitCode}" : combined)}");
        }

        var dllMatch = CopiedDllRegex.Match(combined);
        if (dllMatch.Success)
        {
            onProgress?.Invoke("PIE finished copying DLL.", 95);
            return dllMatch.Groups[1].Value.Trim();
        }

        if (!Regex.IsMatch(combined, "Install complete|Found package:", RegexOptions.IgnoreCase))
        {
            throw new InvalidOperationException($"PIE finished without copying a DLL:\n{combined}");
        }

        onProgress?.Invoke("PIE install complete.", 95);
        return combined;
    }

    private PieRunner? FindPieRunner(string targetPackageId)
    {
        var candidates = _registry.List(PackageType.Php)
            .OrderByDescending(static p => PhpMajorMinor(p.Id))
            .ToList();

        var ordered = candidates
            .Where(p => string.Equals(p.Id, targetPackageId, StringComparison.OrdinalIgnoreCase))
            .Concat(candidates.Where(p => !string.Equals(p.Id, targetPackageId, StringComparison.OrdinalIgnoreCase)));

        foreach (var entry in ordered)
        {
            if (PhpMajorMinor(entry.Id) < 801)
            {
                continue;
            }

            var exe = ResolvePhpExecutable(entry.InstallPath);
            if (exe is not null)
            {
                return new PieRunner(exe, entry.InstallPath, entry.Id);
            }
        }

        return null;
    }

    private static int PhpMajorMinor(string packageId)
    {
        var match = Regex.Match(packageId, @"^php-(\d+)\.(\d+)");
        return match.Success
            ? int.Parse(match.Groups[1].Value) * 100 + int.Parse(match.Groups[2].Value)
            : 0;
    }

    private static string? ResolvePhpExecutable(string installPath)
    {
        var root = PhpExtensionPolicy.ResolvePackageRoot(installPath);
        var exe = Path.Combine(root, "php.exe");
        return File.Exists(exe) ? exe : null;
    }

    private string EnsurePieTargetIni(string installPath, string packageId)
    {
        var root = PhpExtensionPolicy.ResolvePackageRoot(installPath);
        var extDir = Path.Combine(root, "ext").Replace('\\', '/');
        var iniDir = Path.Combine(_paths.RuntimeRoot, ".cache", "pie-ini", packageId);
        Directory.CreateDirectory(iniDir);
        File.WriteAllText(Path.Combine(iniDir, "php.ini"), $"; Stackroot PIE target{Environment.NewLine}extension_dir=\"{extDir}\"{Environment.NewLine}");
        return iniDir;
    }

    private async Task<PieRunResult> RunPieAsync(
        PieRunner runner,
        IReadOnlyList<string> pieArgs,
        string targetPhpRc,
        CancellationToken cancellationToken)
    {
        var root = PhpExtensionPolicy.ResolvePackageRoot(runner.InstallPath);
        var extDir = Path.Combine(root, "ext");
        var extDirUnix = extDir.Replace('\\', '/');
        var args = new List<string>
        {
            "-n",
            "-d", $"extension_dir={extDirUnix}",
        };

        AddPieRunnerExtension(args, extDir, "openssl");
        AddPieRunnerExtension(args, extDir, "zip");

        if (!IsPieRunnerExtensionLoaded(runner.Executable, extDir, "openssl")
            || !IsPieRunnerExtensionLoaded(runner.Executable, extDir, "zip"))
        {
            throw new InvalidOperationException(
                "PIE requires openssl and zip PHP extensions, but they are missing from this PHP install. " +
                "Reinstall PHP from the PHP page, or install the extension via a direct PECL build when available.");
        }

        args.Add(PiePharPath);
        args.AddRange(pieArgs);

        var psi = ProcessStreamEncoding.Create(runner.Executable, root);

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment["PHPRC"] = targetPhpRc;
        psi.Environment["COMPOSER_CACHE_DIR"] = Path.Combine(_paths.RuntimeRoot, ".cache", "composer");
        var machinePath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        psi.Environment["PATH"] = root + Path.PathSeparator + machinePath;

        using var process = new Process { StartInfo = psi };
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new PieRunResult(process.ExitCode, stdout, stderr);
    }

    private static void AddPieRunnerExtension(List<string> args, string extDir, string extensionId)
    {
        var dllPath = ResolveExtensionDllPath(extDir, extensionId);
        if (dllPath is null)
        {
            return;
        }

        args.Add("-d");
        args.Add($"extension={dllPath.Replace('\\', '/')}");
    }

    private static bool IsPieRunnerExtensionLoaded(string phpExecutable, string extDir, string extensionId)
    {
        var extDirUnix = extDir.Replace('\\', '/');
        var args = new List<string>
        {
            "-n",
            "-d", $"extension_dir={extDirUnix}"
        };

        AddPieRunnerExtension(args, extDir, extensionId);

        args.Add("-r");
        args.Add($"exit(extension_loaded('{extensionId}') ? 0 : 1);");

        var psi = ProcessStreamEncoding.Create(phpExecutable, Path.GetDirectoryName(phpExecutable)!);

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi);
        if (process is null)
        {
            return false;
        }

        process.WaitForExit();
        return process.ExitCode == 0;
    }

    private static string? ResolveExtensionDllPath(string extDir, string extensionId)
    {
        if (!Directory.Exists(extDir))
        {
            return null;
        }

        foreach (var name in new[] { $"php_{extensionId}.dll", $"{extensionId}.dll" })
        {
            var path = Path.Combine(extDir, name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private async Task DownloadFileAsync(string url, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var file = File.Create(destination);
        await stream.CopyToAsync(file, cancellationToken);
    }

    private static string? FindDll(string directory, string dllName)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(Path.GetFileName(file), dllName, StringComparison.OrdinalIgnoreCase))
            {
                return file;
            }
        }

        return null;
    }

    private static void CopyAllDllsFromExtract(string extractDir, params string[] destDirs)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(extractDir, "*.dll", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (!seen.Add(name))
            {
                continue;
            }

            foreach (var destDir in destDirs)
            {
                Directory.CreateDirectory(destDir);
                File.Copy(file, Path.Combine(destDir, name), overwrite: true);
            }
        }
    }

    private sealed record PieRunner(string Executable, string InstallPath, string PackageId);
    private sealed record PieRunResult(int ExitCode, string Stdout, string Stderr);
}
