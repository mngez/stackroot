using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Xunit;

namespace Stackroot.Core.Tests;

public sealed class PackageInstallerPhpFallbackTests
{
    [Fact]
    public void DerivePhpArchiveFallbackUrls_appends_archives_path_for_php_only()
    {
        var php = new PackageEntry
        {
            Id = "php-8.3.32",
            Type = PackageType.Php,
            Remote = new RemoteSource
            {
                Url = "https://windows.php.net/downloads/releases/php-8.3.32-nts-Win32-vs16-x64.zip"
            }
        };

        var source = new PackageSource
        {
            Url = php.Remote.Url,
            Sha256 = "abc"
        };

        var derived = PackageInstaller.DerivePhpArchiveFallbackUrls(php, source).ToArray();
        Assert.Single(derived);
        Assert.Equal(
            "https://windows.php.net/downloads/releases/archives/php-8.3.32-nts-Win32-vs16-x64.zip",
            derived[0]);

        var nginx = php with { Type = PackageType.Nginx, Id = "nginx-1.26.2" };
        Assert.Empty(PackageInstaller.DerivePhpArchiveFallbackUrls(nginx, source));
    }

    [Fact]
    public void TryDerivePhpArchivesUrl_ignores_non_php_hosts_and_existing_archives()
    {
        Assert.False(PackageInstaller.TryDerivePhpArchivesUrl(
            "https://example.com/downloads/releases/php.zip", out _));
        Assert.False(PackageInstaller.TryDerivePhpArchivesUrl(
            "https://windows.php.net/downloads/releases/archives/php.zip", out _));
        Assert.True(PackageInstaller.TryDerivePhpArchivesUrl(
            "https://downloads.php.net/~windows/downloads/releases/php-8.5.8-nts-Win32-vs17-x64.zip",
            out var derived));
        Assert.Contains("/archives/", derived, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAsync_uses_archive_mirror_when_primary_returns_404()
    {
        var root = CreateTempDirectory();
        try
        {
            var zipBytes = CreateMinimalZip();
            var sha = Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant();

            var primary = "https://windows.php.net/downloads/releases/php-test-nts-Win32-vs16-x64.zip";
            var archive = "https://windows.php.net/downloads/releases/archives/php-test-nts-Win32-vs16-x64.zip";

            var handler = new ScriptedHandler(request =>
            {
                if (string.Equals(request.RequestUri!.AbsoluteUri, primary, StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                if (string.Equals(request.RequestUri.AbsoluteUri, archive, StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(zipBytes)
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var installer = new PackageInstaller(
                new PackageInstallerOptions(
                    Path.Combine(root, "resources"),
                    Path.Combine(root, "runtime"),
                    root),
                httpClient: new HttpClient(handler));

            var entry = new PackageEntry
            {
                Id = "php-test",
                Type = PackageType.Php,
                Version = "0.0.1",
                InstallDir = "php/test",
                Executable = "php.exe",
                Source = new PackageSource
                {
                    Type = PackageSourceType.Bundled,
                    Archive = "php/php-test-nts-win-x64.zip"
                },
                Remote = new RemoteSource
                {
                    Url = primary,
                    Sha256 = sha
                }
            };

            var installPath = await installer.InstallAsync(entry);
            Assert.True(Directory.Exists(installPath));
            Assert.Contains(primary, handler.RequestedUrls, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(archive, handler.RequestedUrls, StringComparer.OrdinalIgnoreCase);
            Assert.NotNull(installer.GetRegistry().GetById(entry.Id));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task InstallAsync_does_not_register_on_checksum_mismatch()
    {
        var root = CreateTempDirectory();
        try
        {
            var zipBytes = CreateMinimalZip();
            var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(zipBytes)
            });

            var installer = new PackageInstaller(
                new PackageInstallerOptions(
                    Path.Combine(root, "resources"),
                    Path.Combine(root, "runtime"),
                    root),
                httpClient: new HttpClient(handler));

            var entry = new PackageEntry
            {
                Id = "php-bad-hash",
                Type = PackageType.Php,
                Version = "0.0.1",
                InstallDir = "php/bad",
                Executable = "php.exe",
                Source = new PackageSource
                {
                    Type = PackageSourceType.Bundled,
                    Archive = "php/php-bad-nts-win-x64.zip"
                },
                Remote = new RemoteSource
                {
                    Url = "https://windows.php.net/downloads/releases/php-bad-nts-Win32-vs16-x64.zip",
                    Sha256 = new string('a', 64)
                }
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() => installer.InstallAsync(entry));
            Assert.Null(installer.GetRegistry().GetById(entry.Id));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static byte[] CreateMinimalZip()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("php.exe");
            using var stream = entry.Open();
            stream.Write("php"u8);
        }

        return ms.ToArray();
    }

    private static string CreateTempDirectory()
        => Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "stackroot-tests", Guid.NewGuid().ToString("N"))).FullName;

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public List<string> RequestedUrls { get; } = [];

        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestedUrls.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(_responder(request));
        }
    }
}
