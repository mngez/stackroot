using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Abstractions.DataDocuments;
using Stackroot.Core.Catalog;
using Stackroot.Core.Databases;
using Stackroot.Core.IO;
using Stackroot.Core.Settings;
using SiteModel = Stackroot.Core.Sites.Models.Site;

namespace Stackroot.Core.Sites.Installers;

public sealed class WordPressSiteInstaller : ISiteInstaller
{
    private const string LatestZipUrl = "https://wordpress.org/latest.zip";

    private readonly InstallRegistryStore _registry;
    private readonly SettingsStore _settingsStore;
    private readonly DatabaseManager _databaseManager;
    private readonly WpCliManager _wpCli;
    private readonly HttpClient _http;
    private readonly string _sitesDataRoot;

    public WordPressSiteInstaller(
        StackrootPaths paths,
        InstallRegistryStore registry,
        SettingsStore settingsStore,
        DatabaseManager databaseManager,
        WpCliManager wpCli)
    {
        _registry = registry;
        _settingsStore = settingsStore;
        _databaseManager = databaseManager;
        _wpCli = wpCli;
        _sitesDataRoot = Path.Combine(paths.DataRoot, "sites");
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5), DefaultRequestHeaders = { { "User-Agent", "Stackroot" } } };
    }

    public string TemplateId => Models.SiteTemplateIds.Wordpress;
    public string DisplayName => "WordPress";
    public string Description => "Install latest WordPress (ZIP download + wp-cli setup)";
    public string Icon => "📘";

    public bool CanInstall(SiteModel site)
    {
        if (!Directory.Exists(site.Path)) return false;
        var entries = Directory.EnumerateFileSystemEntries(site.Path)
            .Where(e =>
            {
                var name = Path.GetFileName(e);
                if (name.Equals("README.txt", StringComparison.OrdinalIgnoreCase)) return false;
                if (name.Equals("index.php", StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            })
            .ToList();
        return entries.Count == 0;
    }

    public async Task<SiteInstallResult> InstallAsync(
        SiteModel site, SiteInstallOptions options,
        Action<InstallerMessage> onMessage, CancellationToken cancel)
    {
        var phpExe = ResolvePhpExe()
            ?? throw new InvalidOperationException("PHP is required. Install a PHP version first.");

        // 1. Ensure wp-cli via catalog
        onMessage(new InstallerMessage { Kind = InstallerMessageKind.Info, Text = "Ensuring wp-cli is installed…" });
        await _wpCli.EnsureInstalledAsync(cancel).ConfigureAwait(false);

        // 2. Download WordPress ZIP directly (wp-cli core download fails on Windows: tar + long paths)
        onMessage(new InstallerMessage { Kind = InstallerMessageKind.Info, Text = "Downloading WordPress…" });
        var zipPath = Path.GetTempFileName() + ".wp.zip";
        var extractTemp = Path.Combine(Path.GetTempPath(), "stackroot-wp-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            await DownloadAsync(LatestZipUrl, zipPath, cancel).ConfigureAwait(false);
            onMessage(new InstallerMessage { Kind = InstallerMessageKind.Success, Text = "Download complete." });

            onMessage(new InstallerMessage { Kind = InstallerMessageKind.Info, Text = "Extracting…" });
            ZipFile.ExtractToDirectory(zipPath, extractTemp);
            var wpDir = Directory.Exists(Path.Combine(extractTemp, "wordpress"))
                ? Path.Combine(extractTemp, "wordpress") : extractTemp;

            onMessage(new InstallerMessage { Kind = InstallerMessageKind.Info, Text = "Moving files…" });
            CopyDirectory(wpDir, site.Path);
            onMessage(new InstallerMessage { Kind = InstallerMessageKind.Success, Text = "WordPress files installed." });
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            try { if (Directory.Exists(extractTemp)) Directory.Delete(extractTemp, true); } catch { }
        }

        if (cancel.IsCancellationRequested) return Fail("Installation cancelled.");

        // 3. Database — create or re-link if it already exists
        var dbName = string.IsNullOrWhiteSpace(options.DatabaseName)
            ? SanitizeDbName(site.Domain) : options.DatabaseName.Trim();
        var settings = _settingsStore.Load();
        var engine = options.WordPress?.DatabaseEngine ?? settings.Databases.ActiveSqlEngine ?? SqlEngine.Mysql;
        var creds = engine == SqlEngine.Mariadb ? settings.Databases.Mariadb : settings.Databases.Mysql;

        onMessage(new InstallerMessage { Kind = InstallerMessageKind.Info, Text = "Creating database '" + dbName + "'…" });
        try
        {
            _databaseManager.Create(dbName, engine, site.Id);
        }
        catch (InvalidOperationException)
        {
            // Database exists from a previous attempt — link it to this site
            _databaseManager.LinkToSite(dbName, site.Id);
            onMessage(new InstallerMessage { Kind = InstallerMessageKind.Warning,
                Text = "Database '" + dbName + "' already exists — linked to this site." });
        }

        // 4. wp config create
        onMessage(new InstallerMessage { Kind = InstallerMessageKind.Info, Text = "Creating wp-config.php…" });
        var (exit, output) = await _wpCli.RunAsync(phpExe, site.Path,
            "config create --dbname=" + dbName + " --dbuser=" + creds.Username + " --dbpass=" + creds.Password + " --dbhost=127.0.0.1",
            null, cancel).ConfigureAwait(false);
        if (exit != 0) throw new InvalidOperationException("wp config create failed:\n" + output);

        // 5. wp core install with user-provided credentials
        var wp = options.WordPress ?? new WordPressInstallConfig
        {
            SiteTitle = site.Name,
            AdminUser = "admin",
            AdminPassword = Guid.NewGuid().ToString("N")[..12],
            AdminEmail = "admin@" + site.Domain
        };

        onMessage(new InstallerMessage { Kind = InstallerMessageKind.Info, Text = "Completing WordPress installation…" });
        (exit, output) = await _wpCli.RunAsync(phpExe, site.Path,
            "core install --url=" + site.Domain + " --title=\"" + wp.SiteTitle + "\" --admin_user=" + wp.AdminUser + " --admin_password=" + wp.AdminPassword + " --admin_email=" + wp.AdminEmail,
            null, cancel).ConfigureAwait(false);
        if (exit != 0) throw new InvalidOperationException("wp core install failed:\n" + output);

        onMessage(new InstallerMessage { Kind = InstallerMessageKind.Success, Text = "WordPress installed." });

        // Save credentials in app data (NOT in site directory)
        var siteDataDir = Path.Combine(_sitesDataRoot, site.Id);
        Directory.CreateDirectory(siteDataDir);
        var document = new WpCredentialsDocument
        {
            SchemaVersion = DataDocumentSchemas.SiteWpCredentials,
            Password = wp.AdminPassword,
            Engine = engine.ToString().ToLowerInvariant(),
            StorageFormat = "plain"
        };
        var credsJson = JsonSerializer.Serialize(document, JsonSerializerConfig.Default);
        File.WriteAllText(Path.Combine(siteDataDir, "wp-credentials.json"), credsJson);

        var url = "http://" + site.Domain;

        return new SiteInstallResult
        {
            Success = true,
            SiteUrl = url,
            AdminUrl = url + "/wp-admin",
            DatabaseName = dbName,
            AdminUser = wp.AdminUser,
            AdminPassword = wp.AdminPassword,
            PostInstallTips = new[]
            {
                "Site: " + url,
                "Admin: " + url + "/wp-admin",
                "User: " + wp.AdminUser,
                "Password: " + wp.AdminPassword
            }
        };
    }

    private async Task DownloadAsync(string url, string dest, CancellationToken cancel)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        await using var target = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);
        await source.CopyToAsync(target, cancel).ConfigureAwait(false);
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    private string? ResolvePhpExe()
    {
        foreach (var pkg in _registry.List(PackageType.Php).OrderByDescending(p => p.Id, StringComparer.OrdinalIgnoreCase))
        {
            var candidates = new[] { Path.Combine(pkg.InstallPath, "php.exe"), Path.Combine(pkg.InstallPath, "bin", "php.exe") };
            var exe = candidates.FirstOrDefault(File.Exists);
            if (exe is not null) return exe;
        }
        return null;
    }

    private static string SanitizeDbName(string domain)
    {
        var sb = new StringBuilder();
        foreach (var c in domain) { if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c); else sb.Append('_'); }
        return string.IsNullOrWhiteSpace(sb.ToString().Trim('_')) ? "wp_site" : sb.ToString().Trim('_');
    }

    private static SiteInstallResult Fail(string message) => new() { Success = false, PostInstallTips = new[] { message } };
}
