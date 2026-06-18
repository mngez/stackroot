using System.Diagnostics;
using System.Text;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Databases;
using Stackroot.Core.Settings;
using Stackroot.Core.Windows;
using SiteModel = Stackroot.Core.Sites.Models.Site;

namespace Stackroot.Core.Sites.Installers;

public sealed class LaravelSiteInstaller : ISiteInstaller
{
    private readonly InstallRegistryStore _registry;
    private readonly SettingsStore _settingsStore;
    private readonly DatabaseManager _databaseManager;
    private readonly ComposerManager _composerManager;
    private readonly string _dataRoot;

    public LaravelSiteInstaller(
        StackrootPaths paths,
        InstallRegistryStore registry,
        SettingsStore settingsStore,
        DatabaseManager databaseManager,
        ComposerManager composerManager)
    {
        _dataRoot = paths.DataRoot;
        _registry = registry;
        _settingsStore = settingsStore;
        _databaseManager = databaseManager;
        _composerManager = composerManager;
    }

    public string TemplateId => Models.SiteTemplateIds.Laravel;
    public string DisplayName => "Laravel";
    public string Description => "Install latest Laravel via Composer";
    public string Icon => "🔺";

    public bool CanInstall(SiteModel site)
    {
        if (!Directory.Exists(site.Path)) return false;
        // Scaffolding may have created doc-root dir + README.txt + index.php — those are fine
        var docRootDir = (site.DocumentRoot ?? ".").TrimEnd('/', '\\');
        var scaffoldingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "README.txt", "index.php"
        };
        if (!string.IsNullOrEmpty(docRootDir) && docRootDir != ".")
            scaffoldingNames.Add(docRootDir);

        var entries = Directory.EnumerateFileSystemEntries(site.Path)
            .Where(e => !scaffoldingNames.Contains(Path.GetFileName(e)))
            .ToList();
        return entries.Count == 0;
    }

    public async Task<SiteInstallResult> InstallAsync(
        SiteModel site,
        SiteInstallOptions options,
        Action<InstallerMessage> onMessage,
        CancellationToken cancel)
    {
        var phpExe = ResolvePhpExe(site.PhpVersionId)
            ?? throw new InvalidOperationException(
                "PHP is required to install Laravel. Install a PHP version from Services first.");

        var composer = await _composerManager.ResolveRunInfoAsync(phpExe, cancel).ConfigureAwait(false);

        var laravelConfig = options.Laravel;

        // 1. composer create-project (plain Laravel – starter kits installed separately)
        var composerArgs = new List<string>(composer.PrefixArguments)
        {
            "create-project", "laravel/laravel", ".", "--prefer-dist", "--no-interaction", "--no-ansi"
        };

        // Remove scaffolding files. If anything else exists, abort.
        var docRootDir = (site.DocumentRoot ?? ".").TrimEnd('/', '\\');
        if (!TryPrepareEmptyDirectory(site.Path, docRootDir, out var extraFiles))
        {
            return Fail($"Directory is not empty. Found: {string.Join(", ", extraFiles)}. " +
                "Delete these files manually or create a fresh site.");
        }

        onMessage(new InstallerMessage { Kind = InstallerMessageKind.Info, Text = "Downloading Laravel via Composer (this may take a minute)…" });
        await RunProcessAsync(
            composer.FileName,
            [.. composerArgs],
            site.Path,
            onMessage,
            cancel).ConfigureAwait(false);

        if (cancel.IsCancellationRequested) return Fail("Installation cancelled.");

        if (!File.Exists(Path.Combine(site.Path, "artisan")))
        {
            return new SiteInstallResult { Success = false };
        }

        onMessage(new InstallerMessage { Kind = InstallerMessageKind.Success, Text = "Laravel files installed." });

        // 1b. Install starter kit (breeze/jetstream) if selected
        if (laravelConfig is not null && laravelConfig.StarterKit != "none")
        {
            var kitPackage = "laravel/" + laravelConfig.StarterKit;
            onMessage(new InstallerMessage { Kind = InstallerMessageKind.Info, Text = $"Installing {laravelConfig.StarterKit} starter kit…" });

            await RunProcessAsync(
                composer.FileName,
                [.. composer.PrefixArguments, "require", kitPackage, "--dev", "--no-interaction", "--no-ansi"],
                site.Path,
                onMessage,
                cancel).ConfigureAwait(false);

            onMessage(new InstallerMessage { Kind = InstallerMessageKind.Info, Text = $"Scaffolding {laravelConfig.StarterKit} with {laravelConfig.Stack}…" });

            await RunProcessAsync(
                phpExe,
                ["artisan", laravelConfig.StarterKit + ":install", laravelConfig.Stack, "--no-interaction", "--no-ansi"],
                site.Path,
                onMessage,
                cancel).ConfigureAwait(false);

            onMessage(new InstallerMessage { Kind = InstallerMessageKind.Success, Text = $"{laravelConfig.StarterKit} installed." });
        }

        // 2. Database
        string? dbName = null;
        var dbEngine = laravelConfig?.DatabaseEngine ?? SqlEngine.Mysql;

        if (options.CreateDatabase && dbEngine != SqlEngine.Sqlite)
        {
            dbName = string.IsNullOrWhiteSpace(options.DatabaseName)
                ? SanitizeDbName(site.Domain)
                : options.DatabaseName.Trim();

            onMessage(new InstallerMessage { Kind = InstallerMessageKind.Info, Text = $"Creating database '{dbName}'…" });

            var settings = _settingsStore.Load();
            var engine = laravelConfig?.DatabaseEngine ?? settings.Databases.ActiveSqlEngine ?? SqlEngine.Mysql;

            MysqlDatabaseClient.CreateDatabase(_registry, settings, engine, dbName);
            _databaseManager.Create(dbName, engine, site.Id);

            onMessage(new InstallerMessage { Kind = InstallerMessageKind.Success, Text = $"Database '{dbName}' created." });

            var envWarning = UpdateEnvDb(site.Path, dbName, settings, engine);
            if (envWarning is not null)
                onMessage(new InstallerMessage { Kind = InstallerMessageKind.Warning, Text = envWarning });
        }

        // 3. Generate app key
        onMessage(new InstallerMessage { Kind = InstallerMessageKind.Info, Text = "Generating application key…" });
        await RunProcessAsync(
            phpExe,
            ["artisan", "key:generate", "--no-interaction", "--no-ansi"],
            site.Path,
            onMessage,
            cancel).ConfigureAwait(false);

        onMessage(new InstallerMessage { Kind = InstallerMessageKind.Success, Text = "Application key generated." });

        // 4. npm install + build (if Node.js is available and user opted in)
        if (laravelConfig?.RunNpmBuild is true)
        {
            var npmPath = FindNpm();
            if (npmPath is not null)
            {
                onMessage(new InstallerMessage { Kind = InstallerMessageKind.Info, Text = "Running npm install…" });
                await RunProcessAsync(npmPath, ["install"], site.Path, onMessage, cancel).ConfigureAwait(false);

                onMessage(new InstallerMessage { Kind = InstallerMessageKind.Info, Text = "Running npm run build…" });
                await RunProcessAsync(npmPath, ["run", "build"], site.Path, onMessage, cancel).ConfigureAwait(false);

                onMessage(new InstallerMessage { Kind = InstallerMessageKind.Success, Text = "Frontend assets built." });
            }
            else
            {
                onMessage(new InstallerMessage { Kind = InstallerMessageKind.Warning, Text = "Node.js not found — skipping npm build." });
            }
        }

        // 5. Run migrations if opted in
        if (laravelConfig?.RunMigrations is true)
        {
            onMessage(new InstallerMessage { Kind = InstallerMessageKind.Info, Text = "Running database migrations…" });
            await RunProcessAsync(
                phpExe,
                ["artisan", "migrate", "--force", "--no-interaction", "--no-ansi"],
                site.Path,
                onMessage,
                cancel).ConfigureAwait(false);

            onMessage(new InstallerMessage { Kind = InstallerMessageKind.Success, Text = "Migrations complete." });
        }

        var url = $"https://{site.Domain}";
        if (site.Domain.EndsWith(".test", StringComparison.OrdinalIgnoreCase))
            url = $"http://{site.Domain}";

        return new SiteInstallResult
        {
            Success = true,
            SiteUrl = url,
            DatabaseName = dbName,
            PostInstallTips =
            [
                $"cd {site.Path}",
                "php artisan serve  — or just open the site in your browser",
                "php artisan migrate  — run your migrations"
            ]
        };
    }

    // ---- helpers ----

    private static async Task RunProcessAsync(
        string fileName,
        IReadOnlyList<string> args,
        string workingDir,
        Action<InstallerMessage> onMessage,
        CancellationToken cancel)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start: {fileName}");

        // Read stdout line-by-line for progress
        var stdout = Task.Run(() =>
        {
            var sb = new StringBuilder();
            string? line;
            while ((line = process.StandardOutput.ReadLine()) is not null)
            {
                sb.AppendLine(line);
                if (line.Contains("Installing") || line.Contains("Created") || line.Contains("Applied"))
                    onMessage(new InstallerMessage { Kind = InstallerMessageKind.Progress, Text = line.Trim() });
            }
            return sb.ToString();
        }, cancel);

        var stderr = Task.Run(() => process.StandardError.ReadToEnd(), cancel);

        await process.WaitForExitAsync(cancel).ConfigureAwait(false);
        var err = await stderr.ConfigureAwait(false);

        if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(err))
            throw new InvalidOperationException(err.Trim());
    }

    private static string? UpdateEnvDb(string sitePath, string dbName, AppSettings settings, SqlEngine engine)
    {
        var envPath = Path.Combine(sitePath, ".env");
        if (!File.Exists(envPath))
        {
            var example = Path.Combine(sitePath, ".env.example");
            if (File.Exists(example))
                File.Copy(example, envPath, overwrite: false);
            else
                return "No .env file found — skipping DB configuration.";
        }

        var creds = engine == SqlEngine.Mariadb ? settings.Databases.Mariadb : settings.Databases.Mysql;
        var lines = File.ReadAllLines(envPath).ToList();
        ReplaceOrAppend(lines, "DB_CONNECTION", "mysql");
        ReplaceOrAppend(lines, "DB_HOST", "127.0.0.1");
        ReplaceOrAppend(lines, "DB_PORT", "3306");
        ReplaceOrAppend(lines, "DB_DATABASE", dbName);
        ReplaceOrAppend(lines, "DB_USERNAME", creds.Username);
        ReplaceOrAppend(lines, "DB_PASSWORD", creds.Password);
        File.WriteAllLines(envPath, lines);
        return null;
    }

    private static void ReplaceOrAppend(List<string> lines, string key, string value)
    {
        var prefix = key + "=";
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"{key}={value}";
                return;
            }
        }
        lines.Add($"{key}={value}");
    }

    private static string? FindNpm()
    {
        // Try the Stackroot-managed Node first
        var runtimeBin = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Stackroot", "runtime", "bin");
        var candidate = Path.Combine(runtimeBin, "npm.cmd");
        if (File.Exists(candidate)) return candidate;
        candidate = Path.Combine(runtimeBin, "npm");
        if (File.Exists(candidate)) return candidate;

        // Fall back to system PATH
        var paths = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in paths.Split(Path.PathSeparator))
        {
            var npmPath = Path.Combine(dir.Trim(), "npm.cmd");
            if (File.Exists(npmPath)) return npmPath;
            npmPath = Path.Combine(dir.Trim(), "npm");
            if (File.Exists(npmPath)) return npmPath;
        }

        return null;
    }

    private static bool TryPrepareEmptyDirectory(string sitePath, string docRootDir, out List<string> extraFiles)
    {
        // Only delete files WE created (scaffolding). If anything else exists, abort.
        var scaffolding = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "README.txt", "index.php"
        };
        if (!string.IsNullOrEmpty(docRootDir) && docRootDir != ".")
            scaffolding.Add(docRootDir);

        extraFiles = [];

        try
        {
            if (!Directory.Exists(sitePath)) return true;

            foreach (var entry in Directory.EnumerateFileSystemEntries(sitePath))
            {
                var name = Path.GetFileName(entry);
                if (scaffolding.Contains(name))
                {
                    try
                    {
                        if (Directory.Exists(entry))
                            Directory.Delete(entry, true);
                        else
                            File.Delete(entry);
                    }
                    catch { /* best effort */ }
                }
                else
                {
                    extraFiles.Add(name);
                }
            }

            return extraFiles.Count == 0;
        }
        catch
        {
            return false;
        }
    }

    private string? ResolvePhpExe(string? preferredPhpVersionId)
    {
        if (!string.IsNullOrWhiteSpace(preferredPhpVersionId))
        {
            var pkg = _registry.GetById(preferredPhpVersionId);
            var exe = FindPhpExe(pkg?.InstallPath);
            if (exe is not null) return exe;
        }

        foreach (var pkg in _registry.List(PackageType.Php)
                     .OrderByDescending(p => p.Id, StringComparer.OrdinalIgnoreCase))
        {
            var exe = FindPhpExe(pkg.InstallPath);
            if (exe is not null) return exe;
        }

        return null;
    }

    private static string? FindPhpExe(string? installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath)) return null;
        var candidates = new[] { Path.Combine(installPath, "php.exe"), Path.Combine(installPath, "bin", "php.exe") };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string SanitizeDbName(string domain)
    {
        var sb = new StringBuilder();
        foreach (var c in domain)
        {
            if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
            else sb.Append('_');
        }
        var name = sb.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(name) ? "site_db" : name;
    }

    private static SiteInstallResult Fail(string message)
        => new() { Success = false, PostInstallTips = [message] };
}
