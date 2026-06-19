using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Stackroot.App.Services.AppUpdate;

public sealed record AppReleaseInfo(string Version, string InstallerDownloadUrl, string TagName);

public sealed class GitHubAppReleaseClient
{
    private const string DefaultRepo = "mngez/stackroot";
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    static GitHubAppReleaseClient()
    {
        HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Stackroot");
        HttpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<AppReleaseInfo?> GetLatestReleaseAsync(
        string repo = DefaultRepo,
        CancellationToken cancellationToken = default)
    {
        using var response = await HttpClient
            .GetAsync($"https://api.github.com/repos/{repo}/releases/latest", cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var root = document.RootElement;
        if (!root.TryGetProperty("tag_name", out var tagElement))
        {
            return null;
        }

        var tagName = tagElement.GetString();
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return null;
        }

        if (!AppVersion.TryParse(tagName, out var version))
        {
            return null;
        }

        var versionLabel = version.ToString(3);
        string? downloadUrl = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            var expectedName = $"Stackroot-Setup-{versionLabel}.exe";
            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var nameElement))
                {
                    continue;
                }

                var name = nameElement.GetString();
                if (!string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (asset.TryGetProperty("browser_download_url", out var urlElement))
                {
                    downloadUrl = urlElement.GetString();
                }

                break;
            }
        }

        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            return null;
        }

        return new AppReleaseInfo(versionLabel, downloadUrl, tagName.Trim());
    }

    public async Task DownloadInstallerAsync(
        string downloadUrl,
        string destinationPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var response = await HttpClient
            .GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);

        var buffer = new byte[81920];
        long downloaded = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            downloaded += read;
            if (totalBytes > 0)
            {
                progress?.Report(downloaded / (double)totalBytes);
            }
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(1);
    }
}
