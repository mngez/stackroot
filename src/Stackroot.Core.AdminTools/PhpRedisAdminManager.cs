using System.Text;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Nginx;
using Stackroot.Core.Services;
using Stackroot.Core.Services.Php;
using Stackroot.Core.Settings;
using Stackroot.Core.Windows;

namespace Stackroot.Core.AdminTools;

public sealed class PhpRedisAdminManager
{
    public const string NginxConfFileName = "stackroot-phpredisadmin.conf";
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly StackrootPaths _paths;
    private readonly SettingsStore _settingsStore;
    private readonly InstallRegistryStore _registryStore;
    private readonly PackageCatalogStore _catalogStore;
    private readonly PhpConfigWriter _phpConfigWriter;
    private readonly IProcessJobManager _jobManager;
    private string? _pathModeNginxLocations;

    public PhpRedisAdminManager(
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

    public string? GetPathModeNginxLocations() => _pathModeNginxLocations;

    public PhpRedisAdminStatus GetStatus()
    {
        var settings = _settingsStore.Load();
        var pra = NormalizeSettings(settings.Phpredisadmin, settings.General.AppDomain);
        var resolved = ResolveActivePackage(pra);
        var installed = resolved.Installed is not null;
        var phpRequirement = AdminToolPhpResolver.FormatPhpRequirement(PhpRequirementForPackage(resolved.PackageId));
        var nginxPort = ResolveNginxPort(settings);
        var url = pra.Enabled && installed ? BuildUrl(pra, nginxPort, settings.General.AppDomain) : string.Empty;
        var resolvedPhpVersionId = installed && pra.Enabled ? ResolvePhpVersionId(pra) : pra.PhpVersionId;
        var phpVersionCompatible = resolvedPhpVersionId is null
            ? (bool?)null
            : AdminToolPhpResolver.IsPhpCompatible(resolvedPhpVersionId, PhpRequirementForPackage(resolved.PackageId));

        var baseStatus = new PhpRedisAdminStatus
        {
            Enabled = pra.Enabled,
            PackageInstalled = installed,
            PackageId = resolved.PackageId,
            BaseDomain = ResolveBaseDomain(pra, settings.General.AppDomain),
            Path = pra.Path,
            AccessMode = pra.AccessMode,
            Url = url,
            OpenLabel = BuildOpenLabel(pra, settings.General.AppDomain),
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
                Message = "Install a version from Tools."
            };
        }

        if (!pra.Enabled)
        {
            return baseStatus with
            {
                Ready = false,
                Configured = false,
                Message = "phpRedisAdmin is disabled in settings."
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

        if (!IsRedisReady(settings))
        {
            return baseStatus with
            {
                Ready = false,
                Configured = false,
                Message = "Enable Redis service to connect phpRedisAdmin."
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

    public PhpRedisAdminSettings ApplyConfig(PhpRedisAdminConfigUpdate update)
    {
        var settings = _settingsStore.Load();
        var current = NormalizeSettings(settings.Phpredisadmin, settings.General.AppDomain);
        _settingsStore.UpdatePhpRedisAdmin(new PhpRedisAdminSettings
        {
            Enabled = update.Enabled ?? current.Enabled,
            BaseDomain = string.IsNullOrWhiteSpace(update.BaseDomain) ? current.BaseDomain : update.BaseDomain.Trim(),
            AccessMode = update.AccessMode ?? current.AccessMode,
            Subdomain = string.IsNullOrWhiteSpace(update.Subdomain) ? current.Subdomain : update.Subdomain.Trim(),
            Path = string.IsNullOrWhiteSpace(update.Path) ? current.Path : update.Path.Trim('/').Trim(),
            PackageId = string.IsNullOrWhiteSpace(update.PackageId) ? current.PackageId : update.PackageId.Trim(),
            PhpVersionId = update.PhpVersionId ?? current.PhpVersionId
        });

        return _settingsStore.Load().Phpredisadmin;
    }

    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settingsStore.Load();
        var pra = NormalizeSettings(settings.Phpredisadmin, settings.General.AppDomain);

        if (!pra.Enabled)
        {
            ClearPathModeNginxLocations();
            RemoveNginxConfig();
            AdminToolPathLinker.RemovePathTool(_paths, pra.Path);
            return;
        }

        var resolved = ResolveActivePackage(pra);
        if (resolved.Installed is null)
        {
            ClearPathModeNginxLocations();
            RemoveNginxConfig();
            AdminToolPathLinker.RemovePathTool(_paths, pra.Path);
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

        var phpVersionId = ResolvePhpVersionId(pra);
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

        if (!IsRedisReady(settings))
        {
            ClearPathModeNginxLocations();
            RemoveNginxConfig();
            return;
        }

        try
        {
            ComposerDependencyInstaller.EnsureVendor(_registryStore, webRoot, phpVersionId);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Composer dependencies for phpRedisAdmin failed: {ex.Message}", ex);
        }

        var redis = settings.Services.TryGetValue(ServiceId.Redis, out var redisSettings)
            ? redisSettings
            : SettingsDefaults.DefaultServices()[ServiceId.Redis];
        WritePhpRedisAdminConfig(webRoot, redis);

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

        var fcgiPort = ResolveFastCgiPort(settings, phpVersionId);
        if (fcgiPort is null)
        {
            ClearPathModeNginxLocations();
            RemoveNginxConfig();
            AdminToolPathLinker.RemovePathTool(_paths, pra.Path);
            return;
        }

        var nginxPort = ResolveNginxPort(settings);
        WriteNginxConfig(pra, settings.General.AppDomain, webRoot, settings.Php, phpIniPath, fcgiPort.Value, nginxPort, phpVersionId);
    }

    public IReadOnlyList<string> HostsDomains()
    {
        var settings = _settingsStore.Load();
        var pra = NormalizeSettings(settings.Phpredisadmin, settings.General.AppDomain);
        if (!pra.Enabled)
        {
            return [];
        }

        return [ResolveBaseDomain(pra, settings.General.AppDomain)];
    }

    private string? WritePhpConfig(AppSettings settings, string phpVersionId)
    {
        return _phpConfigWriter.WritePhpConfig(
            settings,
            phpVersionId,
            PhpConfigProfile.PhpMyAdmin,
            PhpSessionsPath());
    }

    private void WriteNginxConfig(
        PhpRedisAdminSettings pra,
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
        var phpRc = PhpConfigPaths.ToNginxPhpRc(phpIniPath);
        var serverName = ResolveBaseDomain(pra, appDomain);

        if (pra.AccessMode == AccessMode.Path)
        {
            var pathSegment = string.IsNullOrWhiteSpace(pra.Path) ? "phpredisadmin" : pra.Path.Trim('/');
            var appHtmlRoot = AdminToolPathLinker.LinkPathTool(_paths, root, pathSegment);
            _pathModeNginxLocations = BuildPathModeNginxLocations(pra, appHtmlRoot, fcgiHost, fcgiPort, phpRc, phpVersionId);
            RemoveNginxConfig();
            return;
        }

        _pathModeNginxLocations = null;
        var sb = new StringBuilder();
        sb.AppendLine($"# phpRedisAdmin — {serverName}");
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
        sb.AppendLine($"        fastcgi_pass   {fcgiHost}:{fcgiPort};");
        sb.AppendLine("        fastcgi_index  index.php;");
        sb.AppendLine("        fastcgi_param  SCRIPT_FILENAME $document_root$fastcgi_script_name;");
        sb.AppendLine("        include        fastcgi_params;");
        sb.AppendLine($"        fastcgi_param  PHPRC \"{phpRc}\";");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        File.WriteAllText(confPath, sb.ToString(), Utf8NoBom);
    }

    private static string BuildPathModeNginxLocations(
        PhpRedisAdminSettings pra,
        string appHtmlRoot,
        string fcgiHost,
        int fcgiPort,
        string phpRc,
        string? phpVersionId = null)
    {
        var root = appHtmlRoot.Replace('\\', '/');
        var pathSegment = string.IsNullOrWhiteSpace(pra.Path) ? "phpredisadmin" : pra.Path.Trim('/');
        var versionComment = string.IsNullOrWhiteSpace(phpVersionId)
            ? string.Empty
            : $"# PHP {phpVersionId} → {fcgiHost}:{fcgiPort}{Environment.NewLine}        ";
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
        sb.AppendLine($"        fastcgi_pass   {fcgiHost}:{fcgiPort};");
        sb.AppendLine("        fastcgi_index  index.php;");
        sb.AppendLine("        include        fastcgi_params;");
        sb.AppendLine($"        fastcgi_param  SCRIPT_FILENAME $document_root/{pathSegment}/$1;");
        sb.AppendLine($"        fastcgi_param  PHPRC \"{phpRc}\";");
        sb.AppendLine("    }");
        sb.AppendLine();
        return sb.ToString();
    }

    private static void WritePathModeNginxConfig(
        PhpRedisAdminSettings pra,
        string serverName,
        string root,
        string fcgiHost,
        int fcgiPort,
        int nginxPort,
        string phpRc,
        string confPath)
    {
        var pathSegment = string.IsNullOrWhiteSpace(pra.Path) ? "phpredisadmin" : pra.Path.Trim('/');
        var sb = new StringBuilder();
        sb.AppendLine($"# phpRedisAdmin path mode — {serverName}/{pathSegment}");
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

    private void ClearPathModeNginxLocations() => _pathModeNginxLocations = null;

    private static void WritePhpRedisAdminConfig(string webRoot, ServicePortSettings redis)
    {
        var includesDir = Path.Combine(webRoot, "includes");
        Directory.CreateDirectory(includesDir);

        var host = string.IsNullOrWhiteSpace(redis.Host) ? "127.0.0.1" : redis.Host.Trim();
        var port = redis.Port <= 0 ? 6379 : redis.Port;
        var configPath = Path.Combine(includesDir, "config.inc.php");

        var lines = new[]
        {
            "<?php",
            "/** Generated by Stackroot — do not edit manually */",
            "",
            "$config = array(",
            "  'servers' => array(",
            "    array(",
            $"      'name'   => '{EscapePhpString("Stackroot Redis")}',",
            $"      'host'   => '{EscapePhpString(host)}',",
            $"      'port'   => {port},",
            "      'filter' => '*',",
            "      'scheme' => 'tcp',",
            "      'path'   => '',",
            "      'hide'   => false,",
            "    ),",
            "  ),",
            "",
            "  'seperator' => ':',",
            "  'showEmptyNamespaceAsKey' => false,",
            "  'hideEmptyDBs' => false,",
            "  'cookie_auth' => false,",
            "  'maxkeylen' => 100,",
            "  'count_elements_page' => 100,",
            "  'keys' => false,",
            "  'scansize' => 1000,",
            "  'scanmax' => 0,",
            ");",
            ""
        };

        File.WriteAllLines(configPath, lines, Encoding.UTF8);
    }

    private static string EscapePhpString(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal);

    private string PhpSessionsPath() => Path.Combine(_paths.RuntimeRoot, "sessions", "phpredisadmin");

    private (string PackageId, InstalledPackage? Installed) ResolveActivePackage(PhpRedisAdminSettings pra)
    {
        var configured = string.IsNullOrWhiteSpace(pra.PackageId)
            ? SettingsDefaults.DefaultPhpRedisAdminPackageId
            : pra.PackageId.Trim();
        var configuredInstall = _registryStore.GetById(configured);
        if (configuredInstall is not null)
        {
            return (configured, configuredInstall);
        }

        var best = _registryStore
            .List(PackageType.Phpredisadmin)
            .OrderByDescending(p => p.Version, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (best is not null)
        {
            if (!string.Equals(best.Id, configured, StringComparison.OrdinalIgnoreCase))
            {
                ApplyConfig(new PhpRedisAdminConfigUpdate { PackageId = best.Id });
            }

            return (best.Id, best);
        }

        return (configured, null);
    }

    private string? ResolvePhpVersionId(PhpRedisAdminSettings pra)
    {
        var packageId = ResolveActivePackage(pra).PackageId;
        var requirement = PhpRequirementForPackage(packageId);
        return AdminToolPhpResolver.ResolveVersionId(
            pra.PhpVersionId,
            requirement,
            _settingsStore.Load(),
            _registryStore);
    }

    private RequiresPhp PhpRequirementForPackage(string packageId) =>
        AdminToolPhpResolver.RequirementForPhpRedisAdmin(_catalogStore, packageId);

    private static bool IsRedisReady(AppSettings settings)
    {
        if (!settings.Services.TryGetValue(ServiceId.Redis, out var redis) || !redis.Enabled)
        {
            return false;
        }

        var packageId = string.IsNullOrWhiteSpace(redis.PackageId)
            ? SettingsDefaults.DefaultServices()[ServiceId.Redis].PackageId
            : redis.PackageId;
        return !string.IsNullOrWhiteSpace(packageId);
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

    private static int ResolveNginxPort(AppSettings settings)
    {
        if (settings.Services.TryGetValue(ServiceId.Nginx, out var nginx) && nginx.Port > 0)
        {
            return nginx.Port;
        }

        return SettingsDefaults.DefaultServices()[ServiceId.Nginx].Port;
    }

    private static PhpRedisAdminSettings NormalizeSettings(PhpRedisAdminSettings pra, string? appDomain)
    {
        var normalized = pra with
        {
            BaseDomain = string.IsNullOrWhiteSpace(pra.BaseDomain) ? appDomain?.Trim() ?? "stackroot.test" : pra.BaseDomain.Trim(),
            AccessMode = AccessMode.Path,
            Path = string.IsNullOrWhiteSpace(pra.Path) ? "phpredisadmin" : pra.Path.Trim('/')
        };
        return normalized;
    }

    private static string ResolveBaseDomain(PhpRedisAdminSettings pra, string? appDomain) =>
        string.IsNullOrWhiteSpace(pra.BaseDomain) ? appDomain?.Trim() ?? "stackroot.test" : pra.BaseDomain.Trim();

    private static string BuildUrl(PhpRedisAdminSettings pra, int nginxPort, string? appDomain)
    {
        var host = ResolveBaseDomain(pra, appDomain);
        var portSuffix = nginxPort == 80 ? string.Empty : $":{nginxPort}";
        var segment = string.IsNullOrWhiteSpace(pra.Path) ? "phpredisadmin" : pra.Path.Trim('/');
        return $"http://{host}{portSuffix}/{segment}/";
    }

    private static string BuildOpenLabel(PhpRedisAdminSettings pra, string? appDomain)
    {
        var baseDomain = ResolveBaseDomain(pra, appDomain);
        var segment = string.IsNullOrWhiteSpace(pra.Path) ? "phpredisadmin" : pra.Path.Trim('/');
        return $"{baseDomain}/{segment}";
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
}

public sealed record PhpRedisAdminConfigUpdate
{
    public bool? Enabled { get; init; }
    public string? BaseDomain { get; init; }
    public AccessMode? AccessMode { get; init; }
    public string? Subdomain { get; init; }
    public string? Path { get; init; }
    public string? PackageId { get; init; }
    public string? PhpVersionId { get; init; }
}

public sealed record PhpRedisAdminStatus
{
    public bool Enabled { get; init; }
    public bool Ready { get; init; }
    public bool Configured { get; init; }
    public bool PackageInstalled { get; init; }
    public string PackageId { get; init; } = string.Empty;
    public string BaseDomain { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public AccessMode AccessMode { get; init; } = AccessMode.Path;
    public string Url { get; init; } = string.Empty;
    public string OpenLabel { get; init; } = string.Empty;
    public string? PhpVersionId { get; init; }
    public string PhpRequirement { get; init; } = string.Empty;
    public bool? PhpVersionCompatible { get; init; }
    public string? Message { get; init; }
}

public sealed record CompatiblePhpVersion
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required bool Compatible { get; init; }
    public string? Reason { get; init; }
}
