using System.Text.Json;
using Stackroot.Core.Abstractions;
using Stackroot.Core.IO;

namespace Stackroot.Core.Settings;

public sealed class SettingsStore
{
    private AppSettings? _cache;
    private readonly object _cacheSync = new();
    private readonly JsonFileStore _jsonStore;

    public SettingsStore(string dataRoot, JsonFileStore? jsonStore = null)
    {
        DataRoot = dataRoot;
        _jsonStore = jsonStore ?? new JsonFileStore();
    }

    public string DataRoot { get; }

    public string Path => StackrootPathResolver.SettingsPath(DataRoot);

    public AppSettings Load()
    {
        lock (_cacheSync)
        {
            if (_cache is not null)
            {
                return Clone(_cache);
            }
        }

        var defaults = SettingsDefaults.CreateDefaultSettings();
        if (!File.Exists(Path))
        {
            lock (_cacheSync)
            {
                _cache = defaults;
                return Clone(_cache);
            }
        }

        try
        {
            var stored = _jsonStore.Read<AppSettings>(Path);
            if (stored is null)
            {
                throw new InvalidDataException($"Settings file is empty or invalid: {Path}");
            }

            var merged = MergeWithDefaults(defaults, stored);
            merged.SchemaVersion = SettingsDefaults.SchemaVersion;
            lock (_cacheSync)
            {
                _cache = merged;
                return Clone(_cache);
            }
        }
        catch (Exception ex)
        {
            var backupPath = TryBackupUnreadableFile(Path);
            var backupMessage = string.IsNullOrWhiteSpace(backupPath)
                ? string.Empty
                : $" A backup was saved to '{backupPath}'.";
            throw new InvalidDataException($"Could not read Stackroot settings '{Path}'.{backupMessage}", ex);
        }
    }

    public AppSettings Reload()
    {
        lock (_cacheSync)
        {
            _cache = null;
        }

        return Load();
    }

    public void Save(AppSettings settings)
    {
        settings.SchemaVersion = SettingsDefaults.SchemaVersion;
        _jsonStore.WriteAtomic(Path, settings);
        lock (_cacheSync)
        {
            _cache = Clone(settings);
        }
    }

    public AppSettings UpdateGeneral(GeneralSettings patch)
    {
        var settings = Load();
        settings.General = settings.General with
        {
            WwwPath = patch.WwwPath ?? settings.General.WwwPath,
            AppDomain = patch.AppDomain ?? settings.General.AppDomain,
            PreferredEditor = patch.PreferredEditor ?? settings.General.PreferredEditor,
            CustomEditorPath = patch.CustomEditorPath ?? settings.General.CustomEditorPath,
            CloseBehavior = patch.CloseBehavior ?? settings.General.CloseBehavior,
            LogRetentionDays = patch.LogRetentionDays ?? settings.General.LogRetentionDays,
            AddBinToPath = patch.AddBinToPath ?? settings.General.AddBinToPath,
            ThumbnailsEnabled = patch.ThumbnailsEnabled ?? settings.General.ThumbnailsEnabled,
            LaunchAtStartup = patch.LaunchAtStartup ?? settings.General.LaunchAtStartup,
            DiagnosticsLogEnabled = patch.DiagnosticsLogEnabled ?? settings.General.DiagnosticsLogEnabled,
            DownloadCachePath = patch.DownloadCachePath ?? settings.General.DownloadCachePath
        };

        if (!string.IsNullOrWhiteSpace(settings.General.AppDomain))
        {
            settings.Phpmyadmin.BaseDomain = settings.General.AppDomain!;
            settings.Phpredisadmin.BaseDomain = settings.General.AppDomain!;
        }

        Save(settings);
        return settings;
    }

    public AppSettings UpdateService(ServiceId id, ServicePortSettings patch)
    {
        var settings = Load();
        var current = settings.Services.TryGetValue(id, out var existing)
            ? existing
            : SettingsDefaults.DefaultServices()[id];

        settings.Services[id] = current with
        {
            Enabled = patch.Enabled,
            Host = string.IsNullOrWhiteSpace(patch.Host) ? current.Host : patch.Host,
            Port = patch.Port == 0 ? current.Port : patch.Port,
            SslPort = patch.SslPort ?? current.SslPort,
            SslEnabled = patch.SslEnabled ?? current.SslEnabled,
            AutoStart = patch.AutoStart,
            Supervise = patch.Supervise,
            PackageId = patch.PackageId ?? current.PackageId
        };

        if (id == ServiceId.Mailpit)
        {
            settings.Mailpit = settings.Mailpit with
            {
                Enabled = patch.Enabled,
                SmtpPort = patch.Port == 0 ? settings.Mailpit.SmtpPort : patch.Port,
                PackageId = patch.PackageId ?? settings.Mailpit.PackageId,
                AutoStart = patch.AutoStart,
                Supervise = patch.Supervise
            };
        }

        Save(settings);
        return settings;
    }

    public AppSettings UpdatePhp(PhpSettings patch)
    {
        var settings = Load();
        settings.Php = settings.Php with
        {
            ActiveVersionId = patch.ActiveVersionId ?? settings.Php.ActiveVersionId,
            FpmHost = string.IsNullOrWhiteSpace(patch.FpmHost) ? settings.Php.FpmHost : patch.FpmHost,
            FpmPort = patch.FpmPort <= 0 ? settings.Php.FpmPort : patch.FpmPort,
            Versions = patch.Versions is null ? settings.Php.Versions : new Dictionary<string, PhpVersionSettings>(patch.Versions),
            MemoryLimit = patch.MemoryLimit ?? settings.Php.MemoryLimit,
            MaxExecutionTime = patch.MaxExecutionTime ?? settings.Php.MaxExecutionTime,
            UploadMaxFilesize = patch.UploadMaxFilesize ?? settings.Php.UploadMaxFilesize,
            PostMaxSize = patch.PostMaxSize ?? settings.Php.PostMaxSize,
            Extensions = patch.Extensions is null ? settings.Php.Extensions : new Dictionary<string, bool>(patch.Extensions),
            IniOverrides = patch.IniOverrides is null ? settings.Php.IniOverrides : new Dictionary<string, string>(patch.IniOverrides)
        };

        Save(settings);
        return settings;
    }

    public AppSettings UpdateNode(NodeSettings patch)
    {
        var settings = Load();
        settings.Node = settings.Node with
        {
            NvmPackageId = patch.NvmPackageId ?? settings.Node.NvmPackageId,
            ActiveVersion = patch.ActiveVersion ?? settings.Node.ActiveVersion,
            NpmRegistry = string.IsNullOrWhiteSpace(patch.NpmRegistry) ? settings.Node.NpmRegistry : patch.NpmRegistry,
            AutoUseNvmrc = patch.AutoUseNvmrc,
            PinnedVersions = patch.PinnedVersions ?? settings.Node.PinnedVersions
        };

        Save(settings);
        return settings;
    }

    public AppSettings UpdateSites(SiteDefaults patch)
    {
        var settings = Load();
        settings.Sites = settings.Sites with
        {
            AutoHosts = patch.AutoHosts
        };

        Save(settings);
        return settings;
    }

    public AppSettings UpdateDatabases(DatabaseSettings patch)
    {
        var settings = Load();
        settings.Databases = settings.Databases with
        {
            Mysql = patch.Mysql ?? settings.Databases.Mysql,
            Mariadb = patch.Mariadb ?? settings.Databases.Mariadb,
            ActiveSqlEngine = patch.ActiveSqlEngine ?? settings.Databases.ActiveSqlEngine
        };

        Save(settings);
        return settings;
    }

    public AppSettings UpdatePhpMyAdmin(PhpMyAdminSettings patch)
    {
        var settings = Load();
        settings.Phpmyadmin = settings.Phpmyadmin with
        {
            Enabled = patch.Enabled,
            BaseDomain = string.IsNullOrWhiteSpace(patch.BaseDomain) ? settings.Phpmyadmin.BaseDomain : patch.BaseDomain,
            AccessMode = patch.AccessMode,
            Subdomain = string.IsNullOrWhiteSpace(patch.Subdomain) ? settings.Phpmyadmin.Subdomain : patch.Subdomain,
            Path = string.IsNullOrWhiteSpace(patch.Path) ? settings.Phpmyadmin.Path : patch.Path,
            PackageId = string.IsNullOrWhiteSpace(patch.PackageId) ? settings.Phpmyadmin.PackageId : patch.PackageId,
            PhpVersionId = patch.PhpVersionId ?? settings.Phpmyadmin.PhpVersionId,
            BlowfishSecret = patch.BlowfishSecret ?? settings.Phpmyadmin.BlowfishSecret,
            Domain = patch.Domain ?? settings.Phpmyadmin.Domain
        };

        Save(settings);
        return settings;
    }

    public AppSettings UpdatePhpRedisAdmin(PhpRedisAdminSettings patch)
    {
        var settings = Load();
        settings.Phpredisadmin = settings.Phpredisadmin with
        {
            Enabled = patch.Enabled,
            BaseDomain = string.IsNullOrWhiteSpace(patch.BaseDomain) ? settings.Phpredisadmin.BaseDomain : patch.BaseDomain,
            AccessMode = patch.AccessMode,
            Subdomain = string.IsNullOrWhiteSpace(patch.Subdomain) ? settings.Phpredisadmin.Subdomain : patch.Subdomain,
            Path = string.IsNullOrWhiteSpace(patch.Path) ? settings.Phpredisadmin.Path : patch.Path,
            PackageId = string.IsNullOrWhiteSpace(patch.PackageId) ? settings.Phpredisadmin.PackageId : patch.PackageId,
            PhpVersionId = patch.PhpVersionId ?? settings.Phpredisadmin.PhpVersionId
        };

        Save(settings);
        return settings;
    }

    public AppSettings UpdateMailpit(MailpitSettings patch)
    {
        var settings = Load();
        settings.Mailpit = settings.Mailpit with
        {
            Enabled = patch.Enabled,
            SmtpPort = patch.SmtpPort <= 0 ? settings.Mailpit.SmtpPort : patch.SmtpPort,
            WebPort = patch.WebPort <= 0 ? settings.Mailpit.WebPort : patch.WebPort,
            PackageId = string.IsNullOrWhiteSpace(patch.PackageId) ? settings.Mailpit.PackageId : patch.PackageId,
            AutoStart = patch.AutoStart,
            Supervise = patch.Supervise
        };

        if (settings.Services.TryGetValue(ServiceId.Mailpit, out var mailpitService))
        {
            settings.Services[ServiceId.Mailpit] = mailpitService with
            {
                Enabled = settings.Mailpit.Enabled,
                Port = settings.Mailpit.SmtpPort,
                PackageId = settings.Mailpit.PackageId,
                AutoStart = settings.Mailpit.AutoStart,
                Supervise = settings.Mailpit.Supervise
            };
        }

        Save(settings);
        return settings;
    }

    private static AppSettings MergeWithDefaults(AppSettings defaults, AppSettings? stored)
    {
        if (stored is null)
        {
            defaults.SchemaVersion = SettingsDefaults.SchemaVersion;
            return defaults;
        }

        var merged = defaults with
        {
            General = defaults.General with
            {
                WwwPath = stored.General.WwwPath ?? defaults.General.WwwPath,
                AppDomain = stored.General.AppDomain ?? defaults.General.AppDomain,
                PreferredEditor = stored.General.PreferredEditor ?? defaults.General.PreferredEditor,
                CustomEditorPath = stored.General.CustomEditorPath ?? defaults.General.CustomEditorPath,
                CloseBehavior = stored.General.CloseBehavior ?? defaults.General.CloseBehavior,
                LogRetentionDays = stored.General.LogRetentionDays ?? defaults.General.LogRetentionDays,
                AddBinToPath = stored.General.AddBinToPath ?? defaults.General.AddBinToPath,
                ThumbnailsEnabled = stored.General.ThumbnailsEnabled ?? defaults.General.ThumbnailsEnabled,
                LaunchAtStartup = stored.General.LaunchAtStartup ?? defaults.General.LaunchAtStartup,
                DiagnosticsLogEnabled = stored.General.DiagnosticsLogEnabled ?? defaults.General.DiagnosticsLogEnabled,
                DownloadCachePath = stored.General.DownloadCachePath ?? defaults.General.DownloadCachePath
            },
            Php = defaults.Php with
            {
                ActiveVersionId = stored.Php.ActiveVersionId ?? defaults.Php.ActiveVersionId,
                FpmHost = string.IsNullOrWhiteSpace(stored.Php.FpmHost) ? defaults.Php.FpmHost : stored.Php.FpmHost,
                FpmPort = stored.Php.FpmPort == 0 ? defaults.Php.FpmPort : stored.Php.FpmPort,
                Versions = stored.Php.Versions is null
                    ? defaults.Php.Versions
                    : new Dictionary<string, PhpVersionSettings>(stored.Php.Versions),
                MemoryLimit = stored.Php.MemoryLimit,
                MaxExecutionTime = stored.Php.MaxExecutionTime,
                UploadMaxFilesize = stored.Php.UploadMaxFilesize,
                PostMaxSize = stored.Php.PostMaxSize,
                Extensions = stored.Php.Extensions,
                IniOverrides = stored.Php.IniOverrides
            },
            Node = defaults.Node with
            {
                NvmPackageId = stored.Node.NvmPackageId ?? defaults.Node.NvmPackageId,
                ActiveVersion = stored.Node.ActiveVersion ?? defaults.Node.ActiveVersion,
                NpmRegistry = string.IsNullOrWhiteSpace(stored.Node.NpmRegistry) ? defaults.Node.NpmRegistry : stored.Node.NpmRegistry,
                AutoUseNvmrc = stored.Node.AutoUseNvmrc,
                PinnedVersions = stored.Node.PinnedVersions ?? defaults.Node.PinnedVersions
            },
            Sites = defaults.Sites with
            {
                AutoHosts = stored.Sites.AutoHosts
            },
            Databases = defaults.Databases with
            {
                Mysql = stored.Databases.Mysql ?? defaults.Databases.Mysql,
                Mariadb = stored.Databases.Mariadb ?? defaults.Databases.Mariadb,
                ActiveSqlEngine = stored.Databases.ActiveSqlEngine ?? defaults.Databases.ActiveSqlEngine
            },
            Phpmyadmin = defaults.Phpmyadmin with
            {
                Enabled = stored.Phpmyadmin.Enabled,
                BaseDomain = string.IsNullOrWhiteSpace(stored.Phpmyadmin.BaseDomain) ? defaults.Phpmyadmin.BaseDomain : stored.Phpmyadmin.BaseDomain,
                AccessMode = stored.Phpmyadmin.AccessMode,
                Subdomain = string.IsNullOrWhiteSpace(stored.Phpmyadmin.Subdomain) ? defaults.Phpmyadmin.Subdomain : stored.Phpmyadmin.Subdomain,
                Path = string.IsNullOrWhiteSpace(stored.Phpmyadmin.Path) ? defaults.Phpmyadmin.Path : stored.Phpmyadmin.Path,
                PackageId = string.IsNullOrWhiteSpace(stored.Phpmyadmin.PackageId) ? defaults.Phpmyadmin.PackageId : stored.Phpmyadmin.PackageId,
                PhpVersionId = stored.Phpmyadmin.PhpVersionId ?? defaults.Phpmyadmin.PhpVersionId,
                BlowfishSecret = stored.Phpmyadmin.BlowfishSecret ?? defaults.Phpmyadmin.BlowfishSecret,
                Domain = stored.Phpmyadmin.Domain
            },
            Phpredisadmin = defaults.Phpredisadmin with
            {
                Enabled = stored.Phpredisadmin.Enabled,
                BaseDomain = string.IsNullOrWhiteSpace(stored.Phpredisadmin.BaseDomain) ? defaults.Phpredisadmin.BaseDomain : stored.Phpredisadmin.BaseDomain,
                AccessMode = stored.Phpredisadmin.AccessMode,
                Subdomain = string.IsNullOrWhiteSpace(stored.Phpredisadmin.Subdomain) ? defaults.Phpredisadmin.Subdomain : stored.Phpredisadmin.Subdomain,
                Path = string.IsNullOrWhiteSpace(stored.Phpredisadmin.Path) ? defaults.Phpredisadmin.Path : stored.Phpredisadmin.Path,
                PackageId = string.IsNullOrWhiteSpace(stored.Phpredisadmin.PackageId) ? defaults.Phpredisadmin.PackageId : stored.Phpredisadmin.PackageId,
                PhpVersionId = stored.Phpredisadmin.PhpVersionId ?? defaults.Phpredisadmin.PhpVersionId
            },
            Mailpit = defaults.Mailpit with
            {
                Enabled = stored.Mailpit.Enabled,
                SmtpPort = stored.Mailpit.SmtpPort == 0 ? defaults.Mailpit.SmtpPort : stored.Mailpit.SmtpPort,
                WebPort = stored.Mailpit.WebPort == 0 ? defaults.Mailpit.WebPort : stored.Mailpit.WebPort,
                PackageId = string.IsNullOrWhiteSpace(stored.Mailpit.PackageId) ? defaults.Mailpit.PackageId : stored.Mailpit.PackageId,
                AutoStart = stored.Mailpit.AutoStart,
                Supervise = stored.Mailpit.Supervise
            },
            Services = MergeServices(defaults.Services, stored.Services)
        };

        merged = merged with
        {
            Services = SyncMailpitServiceSettings(merged.Services, merged.Mailpit)
        };

        merged.SchemaVersion = SettingsDefaults.SchemaVersion;
        return merged;
    }

    private static Dictionary<ServiceId, ServicePortSettings> SyncMailpitServiceSettings(
        Dictionary<ServiceId, ServicePortSettings> services,
        MailpitSettings mailpit)
    {
        if (!services.TryGetValue(ServiceId.Mailpit, out var mailpitService))
        {
            return services;
        }

        services[ServiceId.Mailpit] = mailpitService with
        {
            Enabled = mailpit.Enabled,
            Port = mailpit.SmtpPort,
            PackageId = string.IsNullOrWhiteSpace(mailpit.PackageId) ? mailpitService.PackageId : mailpit.PackageId,
            AutoStart = mailpit.AutoStart,
            Supervise = mailpit.Supervise
        };

        return services;
    }

    private static Dictionary<ServiceId, ServicePortSettings> MergeServices(
        Dictionary<ServiceId, ServicePortSettings> defaults,
        Dictionary<ServiceId, ServicePortSettings>? stored)
    {
        var output = new Dictionary<ServiceId, ServicePortSettings>();
        foreach (var (serviceId, fallback) in defaults)
        {
            if (stored is not null && stored.TryGetValue(serviceId, out var current))
            {
                output[serviceId] = fallback with
                {
                    Enabled = current.Enabled,
                    Host = string.IsNullOrWhiteSpace(current.Host) ? fallback.Host : current.Host,
                    Port = current.Port == 0 ? fallback.Port : current.Port,
                    SslPort = current.SslPort ?? fallback.SslPort,
                    SslEnabled = current.SslEnabled ?? fallback.SslEnabled,
                    AutoStart = current.AutoStart,
                    Supervise = current.Supervise,
                    PackageId = current.PackageId ?? fallback.PackageId
                };
            }
            else
            {
                output[serviceId] = fallback;
            }
        }

        return output;
    }

    private static AppSettings Clone(AppSettings value)
    {
        var json = JsonSerializer.Serialize(value, JsonSerializerConfig.Default);
        return JsonSerializer.Deserialize<AppSettings>(json, JsonSerializerConfig.Default)
               ?? SettingsDefaults.CreateDefaultSettings();
    }

    private static string? TryBackupUnreadableFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var backupPath = $"{path}.invalid-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.bak";
            File.Copy(path, backupPath, overwrite: false);
            return backupPath;
        }
        catch
        {
            return null;
        }
    }
}
