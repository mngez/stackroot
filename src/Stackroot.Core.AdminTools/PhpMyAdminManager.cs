using System.Security.Cryptography;
using System.Text;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Nginx;
using Stackroot.Core.Services;
using Stackroot.Core.Services.Php;
using Stackroot.Core.Settings;
using Stackroot.Core.Windows;

namespace Stackroot.Core.AdminTools;

public sealed class PhpMyAdminManager
{
    public const string NginxConfFileName = "stackroot-phpmyadmin.conf";
    public const string PathLocationsFileName = "stackroot-phpmyadmin.locations";
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly StackrootPaths _paths;
    private readonly SettingsStore _settingsStore;
    private readonly InstallRegistryStore _registryStore;
    private readonly PackageCatalogStore _catalogStore;
    private readonly PhpConfigWriter _phpConfigWriter;
    private readonly IProcessJobManager _jobManager;
    private string? _pathModeNginxLocations;

    public PhpMyAdminManager(
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

    public PhpMyAdminStatus GetStatus()
    {
        var settings = _settingsStore.Load();
        var pma = NormalizeSettings(settings.Phpmyadmin, settings.General.AppDomain);
        var resolved = ResolveActivePackage(pma);
        var installed = resolved.Installed is not null;
        var phpRequirement = AdminToolPhpResolver.FormatPhpRequirement(PhpRequirementForPackage(resolved.PackageId));
        var resolvedPhpVersionId = installed && pma.Enabled ? ResolvePhpVersionId(pma) : pma.PhpVersionId;
        var phpVersionCompatible = resolvedPhpVersionId is null
            ? (bool?)null
            : AdminToolPhpResolver.IsPhpCompatible(resolvedPhpVersionId, PhpRequirementForPackage(resolved.PackageId));
        var nginxPort = ResolveNginxPort(settings);
        var url = pma.Enabled && installed ? BuildUrl(pma, nginxPort, settings.General.AppDomain) : string.Empty;

        var baseStatus = new PhpMyAdminStatus
        {
            Enabled = pma.Enabled,
            PackageInstalled = installed,
            PackageId = resolved.PackageId,
            Url = url,
            BaseDomain = pma.BaseDomain,
            Path = pma.Path,
            PhpVersionId = resolvedPhpVersionId,
            PhpRequirement = phpRequirement,
            PhpVersionCompatible = phpVersionCompatible
        };

        if (!installed)
        {
            return baseStatus with
            {
                Ready = false,
                Configured = false,
                Message = "Install phpMyAdmin package first."
            };
        }

        if (!pma.Enabled)
        {
            return baseStatus with
            {
                Ready = false,
                Configured = false,
                Message = "phpMyAdmin is disabled in settings."
            };
        }

        if (string.IsNullOrWhiteSpace(resolvedPhpVersionId) || _registryStore.GetById(resolvedPhpVersionId) is null)
        {
            return baseStatus with
            {
                Ready = false,
                Configured = false,
                Message = $"Install a compatible PHP ({phpRequirement})."
            };
        }

        if (phpVersionCompatible == false)
        {
            return baseStatus with
            {
                Ready = false,
                Configured = false,
                Message = $"Requires {phpRequirement}."
            };
        }

        if (!IsSqlReady(settings))
        {
            return baseStatus with
            {
                Ready = false,
                Configured = false,
                Message = "Enable MySQL or MariaDB with an installed package."
            };
        }

        return baseStatus with
        {
            Ready = true,
            Configured = true
        };
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

    public PhpMyAdminSettings ApplyConfig(PhpMyAdminConfigUpdate update)
    {
        var settings = _settingsStore.Load();
        var current = NormalizeSettings(settings.Phpmyadmin, settings.General.AppDomain);
        var updated = new PhpMyAdminSettings
        {
            Enabled = update.Enabled ?? current.Enabled,
            BaseDomain = string.IsNullOrWhiteSpace(update.BaseDomain) ? current.BaseDomain : update.BaseDomain.Trim(),
            AccessMode = update.AccessMode ?? current.AccessMode,
            Subdomain = string.IsNullOrWhiteSpace(update.Subdomain) ? current.Subdomain : update.Subdomain.Trim(),
            Path = string.IsNullOrWhiteSpace(update.Path) ? current.Path : update.Path.Trim('/').Trim(),
            PackageId = string.IsNullOrWhiteSpace(update.PackageId) ? current.PackageId : update.PackageId.Trim(),
            PhpVersionId = update.PhpVersionId ?? current.PhpVersionId,
            BlowfishSecret = string.IsNullOrWhiteSpace(update.BlowfishSecret) ? current.BlowfishSecret : update.BlowfishSecret.Trim(),
            Domain = string.IsNullOrWhiteSpace(update.Domain) ? current.Domain : update.Domain.Trim()
        };

        _settingsStore.UpdatePhpMyAdmin(updated);
        return _settingsStore.Load().Phpmyadmin;
    }

    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settingsStore.Load();
        var pma = NormalizeSettings(settings.Phpmyadmin, settings.General.AppDomain);

        if (!pma.Enabled)
        {
            ClearPathModeNginxLocations();
            RemoveNginxConfig();
            AdminToolPathLinker.RemovePathTool(_paths, pma.Path);
            return;
        }

        var resolved = ResolveActivePackage(pma);
        if (resolved.Installed is null)
        {
            ClearPathModeNginxLocations();
            RemoveNginxConfig();
            AdminToolPathLinker.RemovePathTool(_paths, pma.Path);
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

        var phpVersionId = ResolvePhpVersionId(pma);
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

        var phpIniPath = WritePhpConfig(settings, phpVersionId);
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
            cancellationToken);
        if (!ensurePhp.Success)
        {
            ClearPathModeNginxLocations();
            RemoveNginxConfig();
            return;
        }

        if (!IsSqlReady(settings))
        {
            ClearPathModeNginxLocations();
            RemoveNginxConfig();
            return;
        }

        var blowfishSecret = ResolveBlowfishSecret(pma);
        var nginxPort = ResolveNginxPort(settings);
        var absoluteUri = BuildUrl(pma, nginxPort, settings.General.AppDomain);
        WritePhpMyAdminConfig(webRoot, blowfishSecret, settings, absoluteUri);

        var fcgiPort = ResolveFastCgiPort(settings, phpVersionId);
        if (fcgiPort is null)
        {
            ClearPathModeNginxLocations();
            RemoveNginxConfig();
            AdminToolPathLinker.RemovePathTool(_paths, pma.Path);
            return;
        }

        WriteNginxConfig(pma, settings.General.AppDomain, webRoot, settings.Php, phpIniPath, fcgiPort.Value, nginxPort, phpVersionId);
    }

    private (string PackageId, InstalledPackage? Installed) ResolveActivePackage(PhpMyAdminSettings pma)
    {
        var configured = string.IsNullOrWhiteSpace(pma.PackageId)
            ? SettingsDefaults.DefaultPhpMyAdminPackageId
            : pma.PackageId.Trim();
        var configuredInstall = _registryStore.GetById(configured);
        if (configuredInstall is not null)
        {
            return (configured, configuredInstall);
        }

        var best = _registryStore
            .List(PackageType.Phpmyadmin)
            .OrderByDescending(p => p.Version, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (best is not null)
        {
            if (!string.Equals(best.Id, configured, StringComparison.OrdinalIgnoreCase))
            {
                ApplyConfig(new PhpMyAdminConfigUpdate { PackageId = best.Id });
            }

            return (best.Id, best);
        }

        return (configured, null);
    }

    private string? ResolvePhpVersionId(PhpMyAdminSettings pma)
    {
        var packageId = ResolveActivePackage(pma).PackageId;
        var requirement = PhpRequirementForPackage(packageId);
        return AdminToolPhpResolver.ResolveVersionId(
            pma.PhpVersionId,
            requirement,
            _settingsStore.Load(),
            _registryStore);
    }

    private string ResolveBlowfishSecret(PhpMyAdminSettings pma)
    {
        if (!string.IsNullOrWhiteSpace(pma.BlowfishSecret) && pma.BlowfishSecret.Length >= 32)
        {
            return pma.BlowfishSecret;
        }

        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))[..32];
        ApplyConfig(new PhpMyAdminConfigUpdate { BlowfishSecret = secret });
        return secret;
    }

    private string? WritePhpConfig(AppSettings settings, string phpVersionId)
    {
        return _phpConfigWriter.WritePhpConfig(
            settings,
            phpVersionId,
            PhpConfigProfile.PhpMyAdmin,
            PhpSessionsPath());
    }

    private void WritePhpMyAdminConfig(
        string webRoot,
        string blowfishSecret,
        AppSettings settings,
        string absoluteUri)
    {
        var configPath = Path.Combine(webRoot, "config.inc.php");
        var lines = new List<string>
        {
            "<?php",
            "/** Generated by Stackroot — do not edit manually */",
            $"$cfg['blowfish_secret'] = '{EscapePhpString(blowfishSecret)}';",
            "$cfg['DefaultLang'] = 'en';",
            "$cfg['ServerDefault'] = 1;",
            "$cfg['UploadDir'] = '';",
            "$cfg['SaveDir'] = '';",
            $"$cfg['PmaAbsoluteUri'] = '{EscapePhpString(absoluteUri)}';",
            string.Empty
        };

        var enabledEngines = ListEnabledSqlEngines(settings);
        var index = 0;
        foreach (var engine in enabledEngines)
        {
            var serviceId = engine == SqlEngine.Mariadb ? ServiceId.Mariadb : ServiceId.Mysql;
            var creds = engine == SqlEngine.Mariadb ? settings.Databases.Mariadb : settings.Databases.Mysql;
            var sqlSettings = settings.Services.TryGetValue(serviceId, out var configured)
                ? configured
                : SettingsDefaults.DefaultServices()[serviceId];
            var host = string.IsNullOrWhiteSpace(sqlSettings.Host) ? "127.0.0.1" : sqlSettings.Host.Trim();
            var port = sqlSettings.Port <= 0 ? 3306 : sqlSettings.Port;
            var label = engine == SqlEngine.Mariadb ? "MariaDB" : "MySQL";
            index++;
            lines.Add($"$i = {index};");
            lines.Add("$cfg['Servers'][$i]['auth_type'] = 'config';");
            lines.Add($"$cfg['Servers'][$i]['verbose'] = '{label}';");
            lines.Add($"$cfg['Servers'][$i]['host'] = '{EscapePhpString(host)}';");
            lines.Add($"$cfg['Servers'][$i]['port'] = '{port}';");
            lines.Add($"$cfg['Servers'][$i]['user'] = '{EscapePhpString(string.IsNullOrWhiteSpace(creds.Username) ? "root" : creds.Username.Trim())}';");
            lines.Add($"$cfg['Servers'][$i]['password'] = '{EscapePhpString(creds.Password ?? string.Empty)}';");
            lines.Add("$cfg['Servers'][$i]['extension'] = 'mysqli';");
            lines.Add("$cfg['Servers'][$i]['AllowNoPassword'] = false;");
            lines.Add(string.Empty);
        }

        if (index == 0)
        {
            var creds = settings.Databases.Mysql;
            lines.Add("$i = 1;");
            lines.Add("$cfg['Servers'][$i]['auth_type'] = 'config';");
            lines.Add("$cfg['Servers'][$i]['verbose'] = 'MySQL';");
            lines.Add("$cfg['Servers'][$i]['host'] = '127.0.0.1';");
            lines.Add("$cfg['Servers'][$i]['port'] = '3306';");
            lines.Add($"$cfg['Servers'][$i]['user'] = '{EscapePhpString(string.IsNullOrWhiteSpace(creds.Username) ? "root" : creds.Username.Trim())}';");
            lines.Add($"$cfg['Servers'][$i]['password'] = '{EscapePhpString(creds.Password ?? string.Empty)}';");
            lines.Add("$cfg['Servers'][$i]['extension'] = 'mysqli';");
            lines.Add("$cfg['Servers'][$i]['AllowNoPassword'] = false;");
            lines.Add(string.Empty);
        }

        File.WriteAllLines(configPath, lines, Utf8NoBom);
    }

    private IReadOnlyList<SqlEngine> ListEnabledSqlEngines(AppSettings settings)
    {
        var engines = new List<SqlEngine>();
        foreach (var engine in new[] { SqlEngine.Mysql, SqlEngine.Mariadb })
        {
            var serviceId = engine == SqlEngine.Mariadb ? ServiceId.Mariadb : ServiceId.Mysql;
            if (!settings.Services.TryGetValue(serviceId, out var sql) || !sql.Enabled)
            {
                continue;
            }

            var packageId = string.IsNullOrWhiteSpace(sql.PackageId)
                ? SettingsDefaults.DefaultServices()[serviceId].PackageId
                : sql.PackageId;
            if (!string.IsNullOrWhiteSpace(packageId) && _registryStore.GetById(packageId!) is not null)
            {
                engines.Add(engine);
            }
        }

        return engines;
    }

    private void WriteNginxConfig(
        PhpMyAdminSettings pma,
        string? appDomain,
        string webRoot,
        PhpSettings php,
        string phpIniPath,
        int fcgiPort,
        int nginxPort,
        string? phpVersionId = null)
    {
        var confDir = Path.Combine(NginxRuntime.nginxPrefix(_paths), "conf", "sites-enabled");
        Directory.CreateDirectory(confDir);
        var confPath = Path.Combine(confDir, NginxConfFileName);

        var root = webRoot.Replace('\\', '/');
        var fcgiHost = string.IsNullOrWhiteSpace(php.FpmHost) ? "127.0.0.1" : php.FpmHost.Trim();
        var fastCgiPass = ResolveFastCgiPass(php, phpVersionId, fcgiPort);
        var phpRc = PhpConfigPaths.ToNginxPhpRc(phpIniPath);
        var serverName = ResolveBaseDomain(pma, appDomain);

        if (pma.AccessMode == AccessMode.Path)
        {
            var pathSegment = string.IsNullOrWhiteSpace(pma.Path) ? "phpmyadmin" : pma.Path.Trim('/');
            var appHtmlRoot = AdminToolPathLinker.LinkPathTool(_paths, root, pathSegment);
            PersistPathModeNginxLocations(BuildPathModeNginxLocations(pma, appHtmlRoot, fastCgiPass, phpRc, phpVersionId));
            RemoveNginxConfig();
            return;
        }

        RemovePathModeNginxLocations();
        _pathModeNginxLocations = null;
        var sb = new StringBuilder();
        sb.AppendLine($"# phpMyAdmin — {serverName}");
        sb.AppendLine("server {");
        sb.AppendLine($"    listen       {nginxPort};");
        sb.AppendLine($"    listen       [::]:{nginxPort};");
        sb.AppendLine($"    server_name  {serverName};");
        sb.AppendLine();
        sb.AppendLine($"    root   {root};");
        sb.AppendLine("    index  index.php;");
        sb.AppendLine();
        sb.AppendLine("    location / {");
        sb.AppendLine("        try_files $uri $uri/ /index.php?$query_string;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    location ~ \\.php$ {");
        sb.AppendLine("        try_files $uri =404;");
        sb.AppendLine($"        fastcgi_pass   {fastCgiPass};");
        sb.AppendLine("        fastcgi_index  index.php;");
        sb.AppendLine("        fastcgi_param  SCRIPT_FILENAME $document_root$fastcgi_script_name;");
        sb.AppendLine("        include        fastcgi_params;");
        sb.AppendLine($"        fastcgi_param  PHPRC \"{phpRc}\";");
        NginxStabilityDirectives.AppendFastCgiLocation(sb);
        sb.AppendLine("    }");
        sb.AppendLine("}");

        File.WriteAllText(confPath, sb.ToString(), Utf8NoBom);
    }

    private static string BuildPathModeNginxLocations(
        PhpMyAdminSettings pma,
        string appHtmlRoot,
        string fastCgiPass,
        string phpRc,
        string? phpVersionId = null)
    {
        var root = appHtmlRoot.Replace('\\', '/');
        var pathSegment = string.IsNullOrWhiteSpace(pma.Path) ? "phpmyadmin" : pma.Path.Trim('/');
        var versionComment = string.IsNullOrWhiteSpace(phpVersionId)
            ? string.Empty
            : $"# PHP {phpVersionId} → {fastCgiPass}{Environment.NewLine}        ";
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

    private static void WritePathModeNginxConfig(
        PhpMyAdminSettings pma,
        string serverName,
        string root,
        string fcgiHost,
        int fcgiPort,
        int nginxPort,
        string phpRc,
        string confPath)
    {
        var pathSegment = string.IsNullOrWhiteSpace(pma.Path) ? "phpmyadmin" : pma.Path.Trim('/');
        var sb = new StringBuilder();
        sb.AppendLine($"# phpMyAdmin path mode — {serverName}/{pathSegment}");
        sb.AppendLine("server {");
        sb.AppendLine($"    listen       {nginxPort};");
        sb.AppendLine($"    listen       [::]:{nginxPort};");
        sb.AppendLine($"    server_name  {serverName};");
        sb.AppendLine();
        sb.AppendLine($"    location = /{pathSegment} {{");
        sb.AppendLine($"        return 301 /{pathSegment}/;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine($"    location /{pathSegment}/ {{");
        sb.AppendLine($"        alias {root}/;");
        sb.AppendLine("        index index.php;");
        sb.AppendLine($"        try_files $uri $uri/ /{pathSegment}/index.php?$query_string;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine($"    location ~ ^/{pathSegment}/(.+\\.php)$ {{");
        sb.AppendLine($"        alias {root}/$1;");
        sb.AppendLine($"        fastcgi_pass   {fcgiHost}:{fcgiPort};");
        sb.AppendLine("        fastcgi_index  index.php;");
        sb.AppendLine("        include        fastcgi_params;");
        sb.AppendLine($"        fastcgi_param  SCRIPT_FILENAME {root}/$1;");
        sb.AppendLine($"        fastcgi_param  PHPRC \"{phpRc}\";");
        NginxStabilityDirectives.AppendFastCgiLocation(sb);
        sb.AppendLine("    }");
        sb.AppendLine("}");

        File.WriteAllText(confPath, sb.ToString(), Utf8NoBom);
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

    private void PersistPathModeNginxLocations(string locations) =>
        File.WriteAllText(PathLocationsFilePath(), locations, Utf8NoBom);

    private void RemovePathModeNginxLocations()
    {
        var path = PathLocationsFilePath();
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private RequiresPhp PhpRequirementForPackage(string packageId) =>
        AdminToolPhpResolver.RequirementForPhpMyAdmin(_catalogStore, packageId);

    private bool IsSqlReady(AppSettings settings)
    {
        var engine = settings.Databases.ActiveSqlEngine ?? SqlEngine.Mysql;
        var serviceId = engine == SqlEngine.Mariadb ? ServiceId.Mariadb : ServiceId.Mysql;
        if (!settings.Services.TryGetValue(serviceId, out var sql) || !sql.Enabled)
        {
            return false;
        }

        var packageId = string.IsNullOrWhiteSpace(sql.PackageId)
            ? SettingsDefaults.DefaultServices()[serviceId].PackageId
            : sql.PackageId;
        return !string.IsNullOrWhiteSpace(packageId) && _registryStore.GetById(packageId!) is not null;
    }

    private static PhpMyAdminSettings NormalizeSettings(PhpMyAdminSettings pma, string? appDomain) =>
        pma with
        {
            BaseDomain = string.IsNullOrWhiteSpace(pma.BaseDomain) ? appDomain?.Trim() ?? "stackroot.test" : pma.BaseDomain.Trim(),
            AccessMode = AccessMode.Path,
            Path = string.IsNullOrWhiteSpace(pma.Path) ? "phpmyadmin" : pma.Path.Trim('/')
        };

    private static string ResolveBaseDomain(PhpMyAdminSettings pma, string? appDomain) =>
        string.IsNullOrWhiteSpace(pma.BaseDomain) ? appDomain?.Trim() ?? "stackroot.test" : pma.BaseDomain.Trim();

    private static string BuildUrl(PhpMyAdminSettings pma, int nginxPort, string? appDomain)
    {
        if (!pma.Enabled)
        {
            return string.Empty;
        }

        var host = ResolveBaseDomain(pma, appDomain);
        var portSuffix = nginxPort == 80 ? string.Empty : $":{nginxPort}";
        var segment = string.IsNullOrWhiteSpace(pma.Path) ? "phpmyadmin" : pma.Path.Trim('/');

        return pma.AccessMode == AccessMode.Subdomain
            ? $"http://{pma.Subdomain}.{host}{portSuffix}/"
            : $"http://{host}{portSuffix}/{segment}/";
    }

    private int? ResolveFastCgiPort(AppSettings settings, string phpVersionId)
    {
        var listeners = PhpCgiRuntime.ActiveListeners();
        if (listeners.TryGetValue(phpVersionId, out var activePort))
        {
            return activePort;
        }

        return PhpCgiPlanner.ResolvePlannedPortForVersion(settings, _registryStore, phpVersionId);
    }

    /// <summary>
    /// The <c>fastcgi_pass</c> target: the nginx upstream fronting the version's php-cgi worker
    /// pool (so nginx load-balances and fails over with zero downtime). Falls back to a literal
    /// host:port only when the version cannot be resolved to an installed package.
    /// </summary>
    private string ResolveFastCgiPass(PhpSettings php, string? phpVersionId, int fcgiPort)
    {
        var canonicalId = string.IsNullOrWhiteSpace(phpVersionId) ? null : _registryStore.GetById(phpVersionId)?.Id;
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

    private static string? ResolveWebRoot(string installPath)
    {
        if (File.Exists(Path.Combine(installPath, "index.php")))
        {
            return installPath;
        }

        if (!Directory.Exists(installPath))
        {
            return null;
        }

        foreach (var entry in Directory.EnumerateDirectories(installPath))
        {
            if (File.Exists(Path.Combine(entry, "index.php")))
            {
                return entry;
            }
        }

        return null;
    }

    private static string EscapePhpString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

    private string PhpSessionsPath() => Path.Combine(_paths.RuntimeRoot, "sessions", "phpmyadmin");
}

public sealed record PhpMyAdminConfigUpdate
{
    public bool? Enabled { get; init; }
    public string? BaseDomain { get; init; }
    public AccessMode? AccessMode { get; init; }
    public string? Subdomain { get; init; }
    public string? Path { get; init; }
    public string? PackageId { get; init; }
    public string? PhpVersionId { get; init; }
    public string? BlowfishSecret { get; init; }
    public string? Domain { get; init; }
}

public sealed record PhpMyAdminStatus
{
    public bool Enabled { get; init; }
    public bool Ready { get; init; }
    public bool Configured { get; init; }
    public bool PackageInstalled { get; init; }
    public string PackageId { get; init; } = string.Empty;
    public string BaseDomain { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string? PhpVersionId { get; init; }
    public string PhpRequirement { get; init; } = string.Empty;
    public bool? PhpVersionCompatible { get; init; }
    public string? Message { get; init; }
}
