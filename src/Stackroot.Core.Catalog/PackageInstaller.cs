using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Catalog;

public delegate void InstallProgressCallback(InstallProgress progress);

public sealed record PackageInstallerOptions(
    string ResourcesRoot,
    string RuntimeRoot,
    string DataRoot,
    string? SevenZipPath = null,
    bool PreferRemote = false);

public sealed class PackageInstaller
{
    private const string UserAgent = "Stackroot/0.1 (+https://stackroot.dev)";
    private static readonly TimeSpan DownloadStallTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(45);

    private readonly PackageInstallerOptions _options;
    private readonly InstallRegistryStore _registry;
    private readonly HttpClient _httpClient;
    private readonly DownloadCacheStore? _downloadCache;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeInstalls = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<InstallProgress>? ProgressChanged;

    public void ReportExternalProgress(InstallProgress progress) => ProgressChanged?.Invoke(this, progress);

    public PackageInstaller(
        PackageInstallerOptions options,
        InstallRegistryStore? registry = null,
        HttpClient? httpClient = null,
        DownloadCacheStore? downloadCache = null)
    {
        _options = options;
        _registry = registry ?? new InstallRegistryStore(options.DataRoot);
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(20)
        };
        _downloadCache = downloadCache;
    }

    public InstallRegistryStore GetRegistry() => _registry;

    public bool IsInstalling(string packageId) =>
        !string.IsNullOrWhiteSpace(packageId) && _activeInstalls.ContainsKey(packageId);

    public void CancelInstall(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return;
        }

        if (_activeInstalls.TryRemove(packageId, out var cts))
        {
            cts.Cancel();
        }
    }

    public Task<string> InstallAsync(
        PackageEntry entry,
        InstallProgressCallback? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        return InstallInternalAsync(entry, onProgress, null, cancellationToken);
    }

    public Task<string> InstallAsync(
        PackageEntry entry,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        return InstallInternalAsync(entry, null, progress, cancellationToken);
    }

    public Task BootstrapResourcesAsync(
        string sourceResourcesRoot,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var packagesSourceDir = Path.Combine(sourceResourcesRoot, "packages");
        if (!Directory.Exists(packagesSourceDir))
        {
            return Task.CompletedTask;
        }

        var packagesDestinationDir = Path.Combine(_options.ResourcesRoot, "packages");
        Directory.CreateDirectory(packagesDestinationDir);

        foreach (var name in new[] { "catalog.json", "php-extensions.json", "pie.phar" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Path.Combine(packagesSourceDir, name);
            if (!File.Exists(source))
            {
                continue;
            }

            var destination = Path.Combine(packagesDestinationDir, name);
            var shouldCopy = !File.Exists(destination)
                || File.GetLastWriteTimeUtc(source) >= File.GetLastWriteTimeUtc(destination);
            if (shouldCopy)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
            }
        }

        return Task.CompletedTask;
    }

    private async Task<string> InstallInternalAsync(
        PackageEntry entry,
        InstallProgressCallback? onProgress,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(InstallTimeout);
        var installToken = timeoutCts.Token;

        if (!_activeInstalls.TryAdd(entry.Id, timeoutCts))
        {
            timeoutCts.Dispose();
            throw new InvalidOperationException($"Package {entry.Id} is already installing.");
        }

        try
        {
            return await InstallInternalCoreAsync(entry, onProgress, progress, installToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || timeoutCts.IsCancellationRequested)
        {
            Emit(onProgress, progress, entry, InstallPhase.Error, 0, "Installation cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            Emit(onProgress, progress, entry, InstallPhase.Error, 0, ex.Message);
            throw;
        }
        finally
        {
            if (_activeInstalls.TryRemove(entry.Id, out var removed))
            {
                removed.Dispose();
            }
        }
    }

    private async Task<string> InstallInternalCoreAsync(
        PackageEntry entry,
        InstallProgressCallback? onProgress,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        Emit(onProgress, progress, entry, InstallPhase.Resolving, 0, "Resolving package source...");

        var source = ResolveSource(entry, _options.PreferRemote);
        var installPath = CatalogPaths.InstallDirForPackage(_options.RuntimeRoot, entry.InstallDir);

        var archivePath = source.Type switch
        {
            PackageSourceType.Bundled => await ResolveBundledArchiveAsync(entry, source, onProgress, progress, cancellationToken),
            PackageSourceType.Remote => await DownloadArchiveAsync(entry, source, onProgress, progress, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported package source type: {source.Type}")
        };

        Emit(onProgress, progress, entry, InstallPhase.Extracting, 70, "Extracting archive...");
        await Task.Run(() => ExtractArchiveToDirectory(archivePath, installPath), cancellationToken)
            .ConfigureAwait(false);

        // Keep downloads in cache for session history (Downloads page)
        if (source.Type == PackageSourceType.Remote && File.Exists(archivePath) && !IsPersistentDownloadPath(archivePath))
        {
            // Archive stays in download cache — visible in Downloads page for the session.
        }

        LinkAfterInstall(entry, installPath);

        Emit(onProgress, progress, entry, InstallPhase.Registering, 95, "Registering installation...");
        _registry.Register(new InstalledPackage
        {
            Id = entry.Id,
            Type = entry.Type,
            Version = entry.Version,
            InstalledAt = DateTimeOffset.UtcNow.ToString("O"),
            InstallPath = installPath,
            Source = source.Type
        });

        Emit(onProgress, progress, entry, InstallPhase.Done, 100, "Installation complete.");
        return installPath;
    }

    public async Task UninstallAsync(PackageEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var installPath = CatalogPaths.InstallDirForPackage(_options.RuntimeRoot, entry.InstallDir);

        try
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                CleanupAfterUninstall(entry, installPath);
                DeleteDirectoryWithRetry(installPath, cancellationToken);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch { /* files may already be gone — still unregister */ }

        _registry.Unregister(entry.Id);
    }

    private static void DeleteDirectoryWithRetry(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        const int maxAttempts = 24;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < maxAttempts - 1)
            {
                Thread.Sleep(250);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts - 1)
            {
                Thread.Sleep(250);
            }
        }
    }

    private void ExtractArchiveToDirectory(string archivePath, string destination)
    {
        var extension = Path.GetExtension(archivePath).ToLowerInvariant();
        if (extension == ".7z")
        {
            Extract7zToDirectory(archivePath, destination);
            return;
        }

        if (extension is ".tgz" or ".gz")
        {
            Extract7zToDirectory(archivePath, destination);
            return;
        }

        if (extension == ".phar")
        {
            InstallPharToDirectory(archivePath, destination);
            return;
        }

        ExtractZipToDirectory(archivePath, destination);
    }

    private static void InstallPharToDirectory(string archivePath, string destination)
    {
        if (Directory.Exists(destination))
        {
            Directory.Delete(destination, recursive: true);
        }

        Directory.CreateDirectory(destination);
        var fileName = Path.GetFileName(archivePath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "package.phar";
        }

        File.Copy(archivePath, Path.Combine(destination, fileName), overwrite: true);
    }

    private static void ExtractZipToDirectory(string archivePath, string destination)
    {
        if (Directory.Exists(destination))
        {
            Directory.Delete(destination, recursive: true);
        }

        Directory.CreateDirectory(destination);

        using var zip = ZipFile.OpenRead(archivePath);
        foreach (var entry in zip.Entries)
        {
            var fullPath = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!fullPath.StartsWith(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Unsafe zip entry path detected: {entry.FullName}");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(fullPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            entry.ExtractToFile(fullPath, overwrite: true);
        }
    }

    private void Extract7zToDirectory(string archivePath, string destination)
    {
        var sevenZip = ResolveSevenZipExecutable();
        if (sevenZip is null)
        {
            throw new FileNotFoundException(
                "7-Zip executable was not found. Set STACKROOT_7Z to 7za.exe/7z.exe, " +
                "or place 7za.exe in resources/tools/7zip/7za.exe.");
        }

        if (Directory.Exists(destination))
        {
            Directory.Delete(destination, recursive: true);
        }

        Directory.CreateDirectory(destination);
        var startInfo = ProcessStreamEncoding.Create(sevenZip);
        startInfo.Arguments = $"x \"{archivePath}\" -o\"{destination}\" -y";
        using var process = new Process { StartInfo = startInfo };

        process.Start();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            var error = process.StandardError.ReadToEnd();
            var output = process.StandardOutput.ReadToEnd();
            throw new InvalidOperationException(
                $"7-Zip extraction failed ({process.ExitCode}): {error}{Environment.NewLine}{output}".Trim());
        }
    }

    private string? ResolveSevenZipExecutable()
    {
        var candidates = new[]
        {
            _options.SevenZipPath,
            Environment.GetEnvironmentVariable("STACKROOT_7Z"),
            Path.Combine(_options.ResourcesRoot, "tools", "7zip", "7za.exe"),
            @"C:\Program Files\7-Zip\7z.exe",
            @"C:\Program Files\7-Zip\7za.exe",
            @"C:\Program Files (x86)\7-Zip\7z.exe",
            @"C:\Program Files (x86)\7-Zip\7za.exe"
        };

        return candidates.FirstOrDefault(static c => !string.IsNullOrWhiteSpace(c) && File.Exists(c));
    }

    private async Task<string> ResolveBundledArchiveAsync(
        PackageEntry entry,
        PackageSource source,
        InstallProgressCallback? onProgress,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.Archive))
        {
            throw new InvalidOperationException($"Bundled archive name is missing for package {entry.Id}.");
        }

        var bundled = CatalogPaths.BundledArchivePath(_options.ResourcesRoot, source.Archive);
        if (File.Exists(bundled))
        {
            return bundled;
        }

        if (entry.Remote is not null)
        {
            Emit(onProgress, progress, entry, InstallPhase.Downloading, 5,
                $"Bundled archive not found locally; downloading from mirror...");
            return await DownloadArchiveAsync(entry, new PackageSource
            {
                Type = PackageSourceType.Remote,
                Url = entry.Remote.Url,
                Mirrors = entry.Remote.Mirrors,
                Sha256 = entry.Remote.Sha256
            }, onProgress, progress, cancellationToken);
        }

        throw new FileNotFoundException($"Bundled archive not found for package {entry.Id}.", bundled);
    }

    private async Task<string> DownloadArchiveAsync(
        PackageEntry entry,
        PackageSource source,
        InstallProgressCallback? onProgress,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.Url))
        {
            throw new InvalidOperationException($"Remote URL is missing for package {entry.Id}.");
        }

        var cacheDir = Path.Combine(_options.RuntimeRoot, ".cache");
        Directory.CreateDirectory(cacheDir);

        var urls = new[] { source.Url }
            .Concat(source.Mirrors ?? [])
            .Where(static url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Exception? lastError = null;
        foreach (var url in urls)
        {
            var extension = Path.GetExtension(new Uri(url).AbsolutePath);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".zip";
            }

            if (_downloadCache?.TryResolveCachedArchive(entry.Id, url, extension, out var cachedPath) == true
                && File.Exists(cachedPath))
            {
                try
                {
                    Emit(onProgress, progress, entry, InstallPhase.Downloading, 60, "Using cached download...");
                    EnsureArchiveSignature(cachedPath, extension);
                    if (!string.IsNullOrWhiteSpace(source.Sha256))
                    {
                        var cachedHash = ComputeSha256(cachedPath);
                        if (!string.Equals(cachedHash, source.Sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException($"Checksum mismatch for cached package {entry.Id}.");
                        }
                    }

                    return cachedPath;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    continue;
                }
            }

            var destination = _downloadCache is not null
                ? _downloadCache.GetDestinationPath(entry.Id, url, extension)
                : Path.Combine(cacheDir, $"{entry.Id}{extension}");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            try
            {
                Emit(onProgress, progress, entry, InstallPhase.Downloading, 10, $"Downloading from {url}...");
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                request.Headers.TryAddWithoutValidation("Accept", "*/*");
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Remote source returned HTML for package {entry.Id}. Check catalog URL or use a mirror.");
                }

                var total = response.Content.Headers.ContentLength;

                await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var file = File.Create(destination))
                {
                    var buffer = new byte[81920];
                    long received = 0;
                    var lastEmitAt = DateTimeOffset.UtcNow;
                    while (true)
                    {
                        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        readCts.CancelAfter(DownloadStallTimeout);
                        int read;
                        try
                        {
                            read = await stream.ReadAsync(buffer, readCts.Token);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            throw new TimeoutException(
                                $"Download stalled for {entry.Id}. No data received in {DownloadStallTimeout.TotalSeconds:0} seconds.");
                        }

                        if (read == 0)
                        {
                            break;
                        }

                        await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        received += read;
                        if (total is > 0)
                        {
                            var percent = (int)Math.Clamp(10 + (received * 50 / total.Value), 10, 60);
                            Emit(onProgress, progress, entry, InstallPhase.Downloading, percent, "Downloading package...");
                        }
                        else if (DateTimeOffset.UtcNow - lastEmitAt > TimeSpan.FromSeconds(2))
                        {
                            lastEmitAt = DateTimeOffset.UtcNow;
                            Emit(onProgress, progress, entry, InstallPhase.Downloading, 15, $"Downloading package... ({received / 1024} KB)");
                        }
                    }
                }

                EnsureArchiveSignature(destination, extension);

                if (!string.IsNullOrWhiteSpace(source.Sha256))
                {
                    var hash = ComputeSha256(destination);
                    if (!string.Equals(hash, source.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(destination);
                        throw new InvalidOperationException($"Checksum mismatch for package {entry.Id}.");
                    }
                }

                _downloadCache?.RegisterDownload(entry.Id, url, destination, source.Sha256);
                return destination;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }
            }
        }

        throw lastError ?? new InvalidOperationException($"Unable to download package {entry.Id}.");
    }

    private bool IsPersistentDownloadPath(string archivePath)
        => _downloadCache?.IsManagedPath(archivePath) == true;

    private static void EnsureArchiveSignature(string filePath, string extension)
    {
        if (extension.Equals(".7z", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(filePath);
            Span<byte> buffer = stackalloc byte[6];
            _ = stream.Read(buffer);
            var is7z = buffer[0] == 0x37
                && buffer[1] == 0x7A
                && buffer[2] == 0xBC
                && buffer[3] == 0xAF
                && buffer[4] == 0x27
                && buffer[5] == 0x1C;
            if (!is7z)
            {
                throw new InvalidOperationException("Downloaded archive is not a valid 7z file.");
            }

            return;
        }

        if (extension is ".tgz" or ".gz")
        {
            using var stream = File.OpenRead(filePath);
            Span<byte> buffer = stackalloc byte[2];
            _ = stream.Read(buffer);
            var isGzip = buffer[0] == 0x1F && buffer[1] == 0x8B;
            if (!isGzip)
            {
                throw new InvalidOperationException("Downloaded archive is not a valid gzip file.");
            }

            return;
        }

        if (extension == ".phar")
        {
            EnsurePharSignature(filePath);
            return;
        }

        using (var stream = File.OpenRead(filePath))
        {
            Span<byte> buffer = stackalloc byte[4];
            _ = stream.Read(buffer);
            var isZip = buffer[0] == 0x50 && buffer[1] == 0x4B;
            if (!isZip)
            {
                throw new InvalidOperationException("Downloaded archive is not a valid zip file.");
            }
        }
    }

    private static void EnsurePharSignature(string filePath)
    {
        var info = new FileInfo(filePath);
        if (info.Length < 512)
        {
            throw new InvalidOperationException("Downloaded PHAR file is too small to be valid.");
        }

        Span<byte> header = stackalloc byte[64];
        using var stream = File.OpenRead(filePath);
        var read = stream.Read(header);
        if (read < 8)
        {
            throw new InvalidOperationException("Downloaded PHAR file is not readable.");
        }

        var isZipPhar = header[0] == 0x50 && header[1] == 0x4B;
        var isGzipPhar = header[0] == 0x1F && header[1] == 0x8B;
        var isStubPhar = header[0] == (byte)'#' && header[1] == (byte)'!';
        if (!isZipPhar && !isGzipPhar && !isStubPhar)
        {
            throw new InvalidOperationException("Downloaded PHAR file does not look valid.");
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static PackageSource ResolveSource(PackageEntry entry, bool preferRemote)
    {
        if (preferRemote && entry.Remote is not null)
        {
            return new PackageSource
            {
                Type = PackageSourceType.Remote,
                Url = entry.Remote.Url,
                Mirrors = entry.Remote.Mirrors,
                Sha256 = entry.Remote.Sha256,
                Size = entry.Remote.Size
            };
        }

        return entry.Source;
    }

    private void LinkAfterInstall(PackageEntry entry, string installPath)
    {
        var channelDir = Path.Combine(_options.RuntimeRoot, entry.Type.ToString().ToLowerInvariant());
        Directory.CreateDirectory(channelDir);
        var currentLink = Path.Combine(channelDir, "current");

        if (Directory.Exists(currentLink) || File.Exists(currentLink))
        {
            try
            {
                Directory.Delete(currentLink, recursive: true);
            }
            catch
            {
                // Non-fatal: link creation is best-effort.
            }
        }

        try
        {
            Directory.CreateSymbolicLink(currentLink, installPath);
        }
        catch
        {
            // Non-fatal on systems without symlink permissions.
        }
    }

    private void CleanupAfterUninstall(PackageEntry entry, string uninstalledPath)
    {
        var currentLink = Path.Combine(_options.RuntimeRoot, entry.Type.ToString().ToLowerInvariant(), "current");
        if (!Directory.Exists(currentLink))
        {
            return;
        }

        try
        {
            var info = new DirectoryInfo(currentLink);
            var target = info.LinkTarget;
            if (string.IsNullOrWhiteSpace(target))
            {
                return;
            }

            var resolvedTarget = Path.GetFullPath(Path.Combine(info.Parent?.FullName ?? _options.RuntimeRoot, target));
            var expected = Path.GetFullPath(uninstalledPath);
            if (string.Equals(resolvedTarget, expected, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(currentLink);
            }
        }
        catch
        {
            // Cleanup hook is best-effort.
        }
    }

    private void Emit(
        InstallProgressCallback? callback,
        IProgress<InstallProgress>? progress,
        PackageEntry entry,
        InstallPhase phase,
        int percent,
        string message)
    {
        var payload = new InstallProgress
        {
            PackageId = entry.Id,
            Phase = phase,
            Percent = percent,
            Message = message
        };

        callback?.Invoke(payload);
        progress?.Report(payload);
        ProgressChanged?.Invoke(this, payload);
    }
}
