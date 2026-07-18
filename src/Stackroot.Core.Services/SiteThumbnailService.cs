using System.IO;
using Microsoft.Playwright;
using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Services;

public sealed class SiteThumbnailService
{
    private readonly string _browsersPath;
    private readonly IDiagnosticsReporter? _diagnostics;
    private IBrowser? _browser;
    private IPlaywright? _playwright;
    private bool _initialized;
    private readonly SemaphoreSlim _captureLock = new(1, 1);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private static readonly TimeSpan ThumbnailCacheTtl = TimeSpan.FromHours(24);

    public SiteThumbnailService(StackrootPaths? paths = null, IDiagnosticsReporter? diagnostics = null)
    {
        _browsersPath = Path.Combine(
            paths?.RuntimeRoot ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Stackroot", "runtime"),
            "playwright-browsers");
        _diagnostics = diagnostics;
    }

    /// <param name="forceRefresh">When true, always capture a new screenshot and replace the saved file (user refresh).</param>
    public async Task<string?> CaptureAsync(string siteUrl, string savePath, bool forceRefresh = false)
    {
        if (!forceRefresh && IsCached(savePath))
        {
            return savePath;
        }

        await _captureLock.WaitAsync();
        try
        {
            if (!forceRefresh && IsCached(savePath))
            {
                return savePath;
            }

            await EnsureBrowserAsync();
            if (_browser is null) return null;
            var dir = Path.GetDirectoryName(savePath);
            if (dir is not null) Directory.CreateDirectory(dir);

            // Screenshot to a temp file first, then atomically move over target
            var tmpPath = savePath + ".tmp";
            var page = await _browser.NewPageAsync();
            await using var _ = page.ConfigureAwait(false);

            await page.SetViewportSizeAsync(1280, 1024);
            await page.GotoAsync(siteUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle,
                Timeout = 15_000
            });

            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = tmpPath,
                FullPage = false,
                Type = ScreenshotType.Png
            });

            // Atomic replacement: delete old, move new
            try { File.Delete(savePath); } catch { }
            File.Move(tmpPath, savePath, overwrite: true);

            return savePath;
        }
        catch (Exception ex)
        {
            _diagnostics?.LogException("Thumbnails", ex);
            return null;
        }
        finally
        {
            _captureLock.Release();
        }
    }

    private static bool IsCached(string savePath)
        => File.Exists(savePath)
           && File.GetLastWriteTimeUtc(savePath) > DateTime.UtcNow.Add(-ThumbnailCacheTtl);

    private async Task EnsureBrowserAsync()
    {
        if (_initialized) return;

        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;

            // Set custom browsers path so Chromium lives under Stackroot's runtime directory
            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", _browsersPath);

            var alreadyInstalled = Directory.Exists(_browsersPath) && Directory.EnumerateFiles(_browsersPath, "chrome.exe", SearchOption.AllDirectories).Any();

            if (!alreadyInstalled)
                _diagnostics?.LogActivity("Thumbnails", "Downloading Chromium browser (~300 MB) for site screenshots…");

            // Run browser install on a background thread to avoid blocking UI
            await Task.Run(() =>
            {
                Microsoft.Playwright.Program.Main(["install", "chromium"]);
            });

            if (!alreadyInstalled)
                _diagnostics?.LogActivity("Thumbnails", "Chromium browser ready");

            _playwright = await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = ["--no-sandbox", "--disable-gpu"]
            });
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }
}
