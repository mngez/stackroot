using System.Text;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Nginx;
using Stackroot.Core.Services;
using Stackroot.Core.Services.Php;
using Stackroot.Core.Settings;
using Stackroot.Core.Windows;

namespace Stackroot.Core.AdminTools;

public sealed class AdminerManager
{
    public const string NginxConfFileName = "stackroot-adminer.conf";
    public const string PathLocationsFileName = "stackroot-adminer.locations";
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly StackrootPaths _paths;
    private readonly SettingsStore _settingsStore;
    private readonly InstallRegistryStore _registryStore;
    private readonly PackageCatalogStore _catalogStore;
    private readonly PhpConfigWriter _phpConfigWriter;
    private readonly IProcessJobManager _jobManager;
    private string? _pathModeNginxLocations;

    public AdminerManager(
        StackrootPaths paths,
        SettingsStore settingsStore,
        InstallRegistryStore registryStore,
        PackageCatalogStore catalogStore,
        PhpConfigWriter phpConfigWriter,
        IProcessJobManager jobManager)
    {
        _paths = paths;
        _settingsStore = settingsStore;
        _registryStore = registryStore;
        _catalogStore = catalogStore;
        _phpConfigWriter = phpConfigWriter;
        _jobManager = jobManager;
    }

    public string? GetPathModeNginxLocations()
    {
        var path = PathLocationsFilePath();
        if (File.Exists(path))
        {
            var fromDisk = File.ReadAllText(path);
            if (!string.IsNullOrWhiteSpace(fromDisk))
            {
                return fromDisk;
            }
        }

        return _pathModeNginxLocations;
    }

    public AdminerStatus GetStatus()
    {
        var settings = _settingsStore.Load();
        var adminer = NormalizeSettings(settings.Adminer, settings.General.AppDomain);
        var resolved = ResolveActivePackage(adminer);
        var installed = resolved.Installed is not null;
        var requirement = PhpRequirementForPackage(resolved.PackageId);
        var phpRequirement = AdminToolPhpResolver.FormatPhpRequirement(requirement);
        var nginxPort = ResolveNginxPort(settings);
        var url = adminer.Enabled && installed ? BuildUrl(adminer, nginxPort, settings.General.AppDomain) : string.Empty;
        var resolvedPhpVersionId = installed && adminer.Enabled ? ResolvePhpVersionId(adminer) : adminer.PhpVersionId;
        var phpVersionCompatible = resolvedPhpVersionId is null
            ? (bool?)null
            : AdminToolPhpResolver.IsPhpCompatible(resolvedPhpVersionId, requirement);

        var baseStatus = new AdminerStatus
        {
            Enabled = adminer.Enabled,
            PackageInstalled = installed,
            PackageId = resolved.PackageId,
            BaseDomain = ResolveBaseDomain(adminer, settings.General.AppDomain),
            Path = adminer.Path,
            Url = url,
            OpenLabel = BuildOpenLabel(adminer, settings.General.AppDomain),
            PhpVersionId = resolvedPhpVersionId,
            PhpRequirement = phpRequirement,
            PhpVersionCompatible = phpVersionCompatible
        };

        if (!installed)
        {
            return baseStatus with
            {
                Ready = false,
                Message = "Install a version from Tools."
            };
        }

        if (!adminer.Enabled)
        {
            return baseStatus with
            {
                Ready = false,
                Message = "Adminer is disabled in settings."
            };
        }

        if (string.IsNullOrWhiteSpace(resolvedPhpVersionId) || _registryStore.GetById(resolvedPhpVersionId) is null)
        {
            return baseStatus with
            {
                Ready = false,
                Message = $"Install a compatible PHP ({phpRequirement})."
            };
        }

        if (phpVersionCompatible == false)
        {
            return baseStatus with
            {
                Ready = false,
                Message = $"Requires {phpRequirement}."
            };
        }

        return baseStatus with { Ready = true };
    }

    public IReadOnlyList<CompatiblePhpVersion> ListCompatiblePhpVersions(string packageId)
    {
        var requirement = PhpRequirementForPackage(packageId);
        return _registryStore
            .List(PackageType.Php)
            .Select(pkg =>
            {
                var compatible = AdminToolPhpResolver.IsPhpCompatible(pkg.Id, requirement);
                return new CompatiblePhpVersion
                {
                    Id = pkg.Id,
                    Label = $"PHP {pkg.Version}",
                    Compatible = compatible,
                    Reason = compatible ? null : $"Requires {AdminToolPhpResolver.FormatPhpRequirement(requirement)}"
                };
            })
            .OrderByDescending(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public AdminerSettings ApplyConfig(AdminerConfigUpdate update)
    {
        var settings = _settingsStore.Load();
        var current = NormalizeSettings(settings.Adminer, settings.General.AppDomain);
        _settingsStore.UpdateAdminer(new AdminerSettings
        {
            Enabled = update.Enabled ?? current.Enabled,
            BaseDomain = string.IsNullOrWhiteSpace(update.BaseDomain) ? current.BaseDomain : update.BaseDomain.Trim(),
            AccessMode = AccessMode.Path,
            Subdomain = string.IsNullOrWhiteSpace(update.Subdomain) ? current.Subdomain : update.Subdomain.Trim(),
            Path = string.IsNullOrWhiteSpace(update.Path) ? current.Path : update.Path.Trim('/').Trim(),
            PackageId = string.IsNullOrWhiteSpace(update.PackageId) ? current.PackageId : update.PackageId.Trim(),
            PhpVersionId = update.PhpVersionId ?? current.PhpVersionId
        });

        return _settingsStore.Load().Adminer;
    }

    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settingsStore.Load();
        var adminer = NormalizeSettings(settings.Adminer, settings.General.AppDomain);

        if (!adminer.Enabled)
        {
            ClearPathModeNginxLocations();
            RemoveNginxConfig();
            AdminToolPathLinker.RemovePathTool(_paths, adminer.Path);
            return;
        }

        var resolved = ResolveActivePackage(adminer);
        if (resolved.Installed is null)
        {
            ClearPathModeNginxLocations();
            RemoveNginxConfig();
            AdminToolPathLinker.RemovePathTool(_paths, adminer.Path);
            return;
        }

        var nginxPackageId = settings.Services.TryGetValue(ServiceId.Nginx, out var nginxSettings)
            ? nginxSettings.PackageId
            : SettingsDefaults.DefaultServices()[ServiceId.Nginx].PackageId;
        var nginxInstalled = string.IsNullOrWhiteSpace(nginxPackageId) ? null : _registryStore.GetById(nginxPackageId);
        if (nginxInstalled is not null)
        {
            NginxRuntime.setupNginxRuntime(_paths, nginxInstalled.InstallPath);
        }

        var webRoot = ResolveWebRoot(resolved.Installed.InstallPath);
        if (webRoot is null)
        {
            ClearPathModeNginxLocations();
            RemoveNginxConfig();
            return;
        }

        WriteAutoLogin(webRoot, settings);

        var phpVersionId = ResolvePhpVersionId(adminer);
        if (string.IsNullOrWhiteSpace(phpVersionId) || _registryStore.GetById(phpVersionId) is null)
        {
            ClearPathModeNginxLocations();
            RemoveNginxConfig();
            return;
        }

        if (!AdminToolPhpResolver.IsPhpCompatible(phpVersionId, PhpRequirementForPackage(resolved.PackageId)))
        {
            ClearPathModeNginxLocations();
            RemoveNginxConfig();
            return;
        }

        var phpIniPath = _phpConfigWriter.WritePhpConfig(
            settings,
            phpVersionId,
            PhpConfigProfile.PhpMyAdmin,
            Path.Combine(_paths.RuntimeRoot, "sessions", "adminer"));
        if (phpIniPath is null)
        {
            ClearPathModeNginxLocations();
            RemoveNginxConfig();
            return;
        }

        var ensurePhp = await PhpCgiRuntime.EnsurePhpFastCgiAsync(
            _paths,
            _registryStore,
            settings,
            _jobManager,
            [phpVersionId],
            cancellationToken,
            pruneAbsentVersions: false);
        if (!ensurePhp.Success)
        {
            ClearPathModeNginxLocations();
            RemoveNginxConfig();
            return;
        }

        var fcgiPort = ResolveFastCgiPort(settings, phpVersionId);
        if (fcgiPort is null)
        {
            ClearPathModeNginxLocations();
            RemoveNginxConfig();
            AdminToolPathLinker.RemovePathTool(_paths, adminer.Path);
            return;
        }

        WriteNginxConfig(adminer, webRoot, settings.Php, phpIniPath, fcgiPort.Value, phpVersionId);
    }

    private void WriteNginxConfig(
        AdminerSettings adminer,
        string webRoot,
        PhpSettings php,
        string phpIniPath,
        int fcgiPort,
        string phpVersionId)
    {
        var root = webRoot.Replace('\\', '/');
        var fastCgiPass = ResolveFastCgiPass(php, phpVersionId, fcgiPort);
        var phpRc = PhpConfigPaths.ToNginxPhpRc(phpIniPath);
        var pathSegment = string.IsNullOrWhiteSpace(adminer.Path) ? "adminer" : adminer.Path.Trim('/');
        var appHtmlRoot = AdminToolPathLinker.LinkPathTool(_paths, root, pathSegment);
        PersistPathModeNginxLocations(BuildPathModeNginxLocations(pathSegment, appHtmlRoot, fastCgiPass, phpRc, phpVersionId));
        RemoveNginxConfig();
    }

    private static string BuildPathModeNginxLocations(
        string pathSegment,
        string appHtmlRoot,
        string fastCgiPass,
        string phpRc,
        string phpVersionId)
    {
        var root = appHtmlRoot.Replace('\\', '/');
        var versionComment = $"# PHP {phpVersionId} → {fastCgiPass}{Environment.NewLine}        ";
        var sb = new StringBuilder();
        sb.AppendLine($"    location = /{pathSegment} {{");
        sb.AppendLine($"        return 301 /{pathSegment}/;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine($"    location /{pathSegment}/ {{");
        sb.AppendLine($"        root {root};");
        sb.AppendLine("        index index.php;");
        sb.AppendLine($"        try_files $uri $uri/ /{pathSegment}/index.php?$query_string;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine($"    location ~ ^/{pathSegment}/(.+\\.php)$ {{");
        sb.AppendLine($"        root {root};");
        sb.AppendLine($"        {versionComment}try_files /{pathSegment}/$1 =404;");
        sb.AppendLine($"        fastcgi_pass   {fastCgiPass};");
        sb.AppendLine("        fastcgi_index  index.php;");
        sb.AppendLine("        include        fastcgi_params;");
        sb.AppendLine($"        fastcgi_param  SCRIPT_FILENAME $document_root/{pathSegment}/$1;");
        sb.AppendLine($"        fastcgi_param  PHPRC \"{phpRc}\";");
        NginxStabilityDirectives.AppendFastCgiLocation(sb);
        sb.AppendLine("    }");
        sb.AppendLine();
        return sb.ToString();
    }

    private void RemoveNginxConfig()
    {
        var confPath = Path.Combine(NginxRuntime.nginxPrefix(_paths), "conf", "sites-enabled", NginxConfFileName);
        if (File.Exists(confPath))
        {
            File.Delete(confPath);
        }
    }

    private void ClearPathModeNginxLocations()
    {
        RemovePathModeNginxLocations();
        _pathModeNginxLocations = null;
    }

    private string PathLocationsFilePath() =>
        Path.Combine(NginxRuntime.nginxPrefix(_paths), "conf", "sites-enabled", PathLocationsFileName);

    private void PersistPathModeNginxLocations(string locations)
    {
        _pathModeNginxLocations = locations;
        var path = PathLocationsFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, locations, Utf8NoBom);
    }

    private void RemovePathModeNginxLocations()
    {
        var path = PathLocationsFilePath();
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private (string PackageId, InstalledPackage? Installed) ResolveActivePackage(AdminerSettings adminer)
    {
        var configured = string.IsNullOrWhiteSpace(adminer.PackageId)
            ? SettingsDefaults.DefaultAdminerPackageId
            : adminer.PackageId.Trim();
        var configuredInstall = _registryStore.GetById(configured);
        if (configuredInstall is not null)
        {
            return (configured, configuredInstall);
        }

        var best = _registryStore
            .List(PackageType.Adminer)
            .OrderByDescending(p => p.Version, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (best is not null)
        {
            if (!string.Equals(best.Id, configured, StringComparison.OrdinalIgnoreCase))
            {
                ApplyConfig(new AdminerConfigUpdate { PackageId = best.Id });
            }

            return (best.Id, best);
        }

        return (configured, null);
    }

    private string? ResolvePhpVersionId(AdminerSettings adminer)
    {
        var packageId = ResolveActivePackage(adminer).PackageId;
        return AdminToolPhpResolver.ResolveVersionId(
            adminer.PhpVersionId,
            PhpRequirementForPackage(packageId),
            _settingsStore.Load(),
            _registryStore);
    }

    private RequiresPhp PhpRequirementForPackage(string packageId) =>
        AdminToolPhpResolver.RequirementForAdminer(_catalogStore, packageId);

    private int? ResolveFastCgiPort(AppSettings settings, string phpVersionId)
    {
        var listeners = PhpCgiRuntime.ActiveListeners();
        if (listeners.TryGetValue(phpVersionId, out var activePort))
        {
            return activePort;
        }

        return PhpCgiPlanner.ResolvePlannedPortForVersion(settings, _registryStore, phpVersionId);
    }

    private string ResolveFastCgiPass(PhpSettings php, string phpVersionId, int fcgiPort)
    {
        var canonicalId = _registryStore.GetById(phpVersionId)?.Id;
        if (!string.IsNullOrWhiteSpace(canonicalId))
        {
            return PhpFastCgiNaming.UpstreamName(canonicalId);
        }

        var host = string.IsNullOrWhiteSpace(php.FpmHost) ? "127.0.0.1" : php.FpmHost.Trim();
        return $"{host}:{fcgiPort}";
    }

    private static int ResolveNginxPort(AppSettings settings)
    {
        if (settings.Services.TryGetValue(ServiceId.Nginx, out var nginx) && nginx.Port > 0)
        {
            return nginx.Port;
        }

        return SettingsDefaults.DefaultServices()[ServiceId.Nginx].Port;
    }

    private static AdminerSettings NormalizeSettings(AdminerSettings adminer, string? appDomain)
    {
        return adminer with
        {
            BaseDomain = string.IsNullOrWhiteSpace(adminer.BaseDomain) ? appDomain?.Trim() ?? "stackroot.test" : adminer.BaseDomain.Trim(),
            AccessMode = AccessMode.Path,
            Path = string.IsNullOrWhiteSpace(adminer.Path) ? "adminer" : adminer.Path.Trim('/')
        };
    }

    private static string ResolveBaseDomain(AdminerSettings adminer, string? appDomain) =>
        string.IsNullOrWhiteSpace(adminer.BaseDomain) ? appDomain?.Trim() ?? "stackroot.test" : adminer.BaseDomain.Trim();

    private static string BuildUrl(AdminerSettings adminer, int nginxPort, string? appDomain)
    {
        var host = ResolveBaseDomain(adminer, appDomain);
        var portSuffix = nginxPort == 80 ? string.Empty : $":{nginxPort}";
        var segment = string.IsNullOrWhiteSpace(adminer.Path) ? "adminer" : adminer.Path.Trim('/');
        return $"http://{host}{portSuffix}/{segment}/";
    }

    private static string BuildOpenLabel(AdminerSettings adminer, string? appDomain)
    {
        var baseDomain = ResolveBaseDomain(adminer, appDomain);
        var segment = string.IsNullOrWhiteSpace(adminer.Path) ? "adminer" : adminer.Path.Trim('/');
        return $"{baseDomain}/{segment}";
    }

    private void WriteAutoLogin(string webRoot, AppSettings settings)
    {
        var adminerFile = Path.Combine(webRoot, "adminer.php");
        var indexFile = Path.Combine(webRoot, "index.php");
        if (!File.Exists(adminerFile) && File.Exists(indexFile) && !IsStackrootEntry(indexFile))
        {
            File.Move(indexFile, adminerFile);
        }

        if (!File.Exists(adminerFile))
        {
            return;
        }

        File.WriteAllText(indexFile, BuildEntryScript(ListLoginServers(settings)), Utf8NoBom);
    }

    private static bool IsStackrootEntry(string path)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[160];
        var read = stream.Read(buffer, 0, buffer.Length);
        var head = Encoding.UTF8.GetString(buffer, 0, read);
        return head.Contains("Stackroot Adminer entry", StringComparison.Ordinal);
    }

    public IReadOnlyList<AdminerLoginServer> ListLoginServers(AppSettings settings)
    {
        var servers = new List<AdminerLoginServer>();
        AddLoginServer(servers, settings, ServiceId.Mysql, "mysql", "MySQL", "server", "root", settings.Databases.Mysql, 3306);
        AddLoginServer(servers, settings, ServiceId.Mariadb, "mariadb", "MariaDB", "server", "root", settings.Databases.Mariadb, 3306);
        AddLoginServer(servers, settings, ServiceId.Postgresql, "postgresql", "PostgreSQL", "pgsql", "postgres", settings.Databases.Postgresql, 5432);
        return servers;
    }

    private void AddLoginServer(
        List<AdminerLoginServer> servers,
        AppSettings settings,
        ServiceId serviceId,
        string key,
        string label,
        string driver,
        string defaultUsername,
        DatabaseCredentials credentials,
        int defaultPort)
    {
        if (!settings.Services.TryGetValue(serviceId, out var service) || !service.Enabled)
        {
            return;
        }

        var packageId = string.IsNullOrWhiteSpace(service.PackageId)
            ? SettingsDefaults.DefaultServices()[serviceId].PackageId
            : service.PackageId;
        if (string.IsNullOrWhiteSpace(packageId) || _registryStore.GetById(packageId) is null)
        {
            return;
        }

        var host = string.IsNullOrWhiteSpace(service.Host) ? "127.0.0.1" : service.Host.Trim();
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            host = "127.0.0.1";
        }

        var port = service.Port <= 0 ? defaultPort : service.Port;
        var username = string.IsNullOrWhiteSpace(credentials.Username) ? defaultUsername : credentials.Username.Trim();
        servers.Add(new AdminerLoginServer(key, label, driver, $"{host}:{port}", username, credentials.Password ?? string.Empty));
    }

    public static string BuildEntryScript(IReadOnlyList<AdminerLoginServer> servers)
    {
        var serverLines = new StringBuilder();
        serverLines.AppendLine("$stackrootServers = array(");
        foreach (var server in servers)
        {
            serverLines.AppendLine($"    {PhpString(server.Key)} => array(");
            serverLines.AppendLine($"        'label' => {PhpString(server.Label)},");
            serverLines.AppendLine($"        'driver' => {PhpString(server.Driver)},");
            serverLines.AppendLine($"        'server' => {PhpString(server.Server)},");
            serverLines.AppendLine($"        'username' => {PhpString(server.Username)},");
            serverLines.AppendLine($"        'password' => {PhpString(server.Password)},");
            serverLines.AppendLine("    ),");
        }

        serverLines.Append(");");

        return $$"""
            <?php
            /** Stackroot Adminer entry — generated, do not edit. */
            {{serverLines}}

            $chosen = null;
            $selected = $_GET['stackroot'] ?? '';
            if ($selected !== '' && isset($stackrootServers[$selected])) {
                $chosen = $stackrootServers[$selected];
            } elseif (count($stackrootServers) === 1) {
                $chosen = reset($stackrootServers);
            }

            if ($chosen !== null && !isset($_GET['username'])) {
                $_POST['auth'] = array(
                    'driver' => $chosen['driver'],
                    'server' => $chosen['server'],
                    'username' => $chosen['username'],
                    'password' => $chosen['password'],
                    'db' => '',
                    'stackroot' => '1',
                );
            }

            function adminer_object()
            {
                class StackrootAdminer extends Adminer\Adminer
                {
                    public function verifyLoginToken()
                    {
                        return (($_POST['auth']['stackroot'] ?? '') !== '1');
                    }

                    public function credentials()
                    {
                        $server = Adminer\SERVER;
                        $username = $_GET['username'] ?? '';
                        foreach ($GLOBALS['stackrootServers'] as $item) {
                            if ($item['driver'] === Adminer\DRIVER && $item['server'] === $server && $item['username'] === $username) {
                                return array($item['server'], $item['username'], $item['password']);
                            }
                        }

                        return array($server, $username, (string) Adminer\get_password());
                    }

                    public function login($login, $password)
                    {
                        return true;
                    }

                    public function loginForm()
                    {
                        echo "<p>Stackroot could not sign in with the saved database credentials. Start the database from the Dashboard, then open Adminer again.</p>";
                    }
                }

                return new StackrootAdminer();
            }

            if ($chosen === null && !isset($_GET['username'])) {
                header('Content-Type: text/html; charset=utf-8');
                echo "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Adminer</title></head><body style=\"font-family:system-ui,sans-serif;max-width:40rem;margin:2rem auto\">";
                if (!$stackrootServers) {
                    echo "<h1>Adminer</h1><p>Install MySQL, MariaDB, or PostgreSQL from Services. The username and password are in Settings, under Databases. Adminer signs in with those.</p>";
                    echo "<p dir=\"rtl\">ثبّت MySQL أو MariaDB أو PostgreSQL من صفحة الخدمات. اسم المستخدم وكلمة المرور في الإعدادات ضمن قواعد البيانات، وAdminer يدخل بهما تلقائياً.</p>";
                } else {
                    echo "<h1>Adminer</h1><p>Choose a database server. Stackroot signs in with the saved username and password, then lists the databases.</p>";
                    echo "<ul>";
                    foreach ($stackrootServers as $key => $item) {
                        $href = '?stackroot=' . rawurlencode($key);
                        echo "<li><a href=\"" . htmlspecialchars($href, ENT_QUOTES) . "\">" . htmlspecialchars($item['label'] . ' — ' . $item['server'], ENT_QUOTES) . "</a></li>";
                    }
                    echo "</ul>";
                }
                echo "</body></html>";
                exit;
            }

            function stackroot_missing_extensions($driver)
            {
                $groups = array(
                    'server' => array('mysqli', 'pdo_mysql'),
                    'pgsql' => array('pgsql', 'pdo_pgsql'),
                );
                if (!isset($groups[$driver])) {
                    return array();
                }

                foreach ($groups[$driver] as $extension) {
                    if (extension_loaded($extension)) {
                        return array();
                    }
                }

                return $groups[$driver];
            }

            $stackrootDriver = '';
            if ($chosen !== null) {
                $stackrootDriver = $chosen['driver'];
            } else {
                foreach (array('pgsql', 'server') as $driverName) {
                    if (isset($_GET[$driverName])) {
                        $stackrootDriver = $driverName;
                        break;
                    }
                }
            }

            $stackrootMissing = stackroot_missing_extensions($stackrootDriver);
            if ($stackrootMissing) {
                header('Content-Type: text/html; charset=utf-8');
                $version = htmlspecialchars(PHP_VERSION, ENT_QUOTES);
                $englishList = htmlspecialchars(implode(' or ', $stackrootMissing), ENT_QUOTES);
                $arabicList = htmlspecialchars(implode(' أو ', $stackrootMissing), ENT_QUOTES);
                echo "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Adminer</title></head><body style=\"font-family:system-ui,sans-serif;max-width:40rem;margin:2rem auto\">";
                echo "<h1>Adminer</h1>";
                echo "<p>PHP $version does not load $englishList. In Stackroot, open PHP, select this version, then Extensions, and turn one of them on. Open Adminer again after that.</p>";
                echo "<p dir=\"rtl\">PHP $version لا يحمّل $arabicList. من Stackroot افتح PHP، اختر هذا الإصدار، ثم الامتدادات، وفعّل أحدها. بعدها أعد فتح Adminer.</p>";
                if (count($stackrootServers) > 1) {
                    echo "<p><a href=\"?\">Choose another server</a></p>";
                }
                echo "</body></html>";
                exit;
            }

            require __DIR__ . '/adminer.php';

            """;
    }

    private static string PhpString(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal);
        return $"'{escaped}'";
    }

    private static string? ResolveWebRoot(string installPath)
    {
        if (File.Exists(Path.Combine(installPath, "index.php")))
        {
            return installPath;
        }

        return null;
    }
}

public sealed record AdminerConfigUpdate
{
    public bool? Enabled { get; init; }
    public string? BaseDomain { get; init; }
    public string? Subdomain { get; init; }
    public string? Path { get; init; }
    public string? PackageId { get; init; }
    public string? PhpVersionId { get; init; }
}

public sealed record AdminerLoginServer(
    string Key,
    string Label,
    string Driver,
    string Server,
    string Username,
    string Password);

public sealed record AdminerStatus
{
    public bool Enabled { get; init; }
    public bool Ready { get; init; }
    public bool PackageInstalled { get; init; }
    public string PackageId { get; init; } = string.Empty;
    public string BaseDomain { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string OpenLabel { get; init; } = string.Empty;
    public string? PhpVersionId { get; init; }
    public string PhpRequirement { get; init; } = string.Empty;
    public bool? PhpVersionCompatible { get; init; }
    public string? Message { get; init; }
}
