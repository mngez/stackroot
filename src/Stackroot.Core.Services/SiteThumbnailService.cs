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
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SiteThumbnailService(StackrootPaths? paths = null, IDiagnosticsReporter? diagnostics = null)
    {
        _browsersPath = Path.Combine(
            paths?.RuntimeRoot ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Stackroot", "runtime"),
            "playwright-browsers");
        _diagnostics = diagnostics;
    }

    public async Task<string?> CaptureAsync(string siteUrl, string savePath)
    {
        await EnsureBrowserAsync();
        if (_browser is null) return null;

        try
        {
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
        catch (Exception)
        {
            return null;
        }
    }

    private async Task EnsureBrowserAsync()
    {
        if (_initialized) return;

        await _lock.WaitAsync();
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
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }
}
