using System.Text.Json;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Dns;
using Stackroot.Core.IO;
using Stackroot.Core.IO.Storage;

namespace Stackroot.Core.Settings;

public enum SettingsLoadIssue
{
    None,
    Corrupted
}

public sealed class SettingsStore
{
    private AppSettings? _cache;
    private readonly object _cacheSync = new();
    private readonly IJsonFileStore _jsonStore;

    public SettingsStore(string dataRoot, IJsonFileStore? jsonStore = null)
    {
        DataRoot = dataRoot;
        _jsonStore = jsonStore ?? new JsonFileStore();
    }

    public string DataRoot { get; }

    public string Path => StackrootPathResolver.SettingsPath(DataRoot);

    /// <summary>
    /// True when defaults are in memory because settings.json could not be read — blocks disk writes.
    /// </summary>
    public bool PersistenceBlocked { get; private set; }

    public AppSettings Load()
    {
        lock (_cacheSync)
        {
            if (_cache is not null)
            {
                return AppSettingsCopier.Detach(_cache);
            }
        }

        return LoadCore(allowRepair: true);
    }

    public bool TryLoad(out AppSettings settings, out SettingsLoadIssue issue)
    {
        lock (_cacheSync)
        {
            if (_cache is not null)
            {
                settings = AppSettingsCopier.Detach(_cache);
                issue = SettingsLoadIssue.None;
                return true;
            }
        }

        try
        {
            settings = LoadCore(allowRepair: true);
            PersistenceBlocked = false;
            issue = SettingsLoadIssue.None;
            return true;
        }
        catch (InvalidDataException)
        {
            PersistenceBlocked = true;
            settings = SettingsDefaults.CreateDefaultSettings();
            settings.SchemaVersion = SettingsDefaults.SchemaVersion;
            lock (_cacheSync)
            {
                _cache = AppSettingsCopier.Detach(settings);
            }

            issue = SettingsLoadIssue.Corrupted;
            return false;
        }
    }

    public string? FindLatestBackupPath()
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        var fileName = System.IO.Path.GetFileName(Path);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(directory, $"{fileName}.*.bak")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    public bool TryRestoreFromLatestBackup(out string? error)
    {
        var backupPath = FindLatestBackupPath();
        if (string.IsNullOrWhiteSpace(backupPath))
        {
            error = "No settings backup file was found.";
            return false;
        }

        return TryRestoreFromBackup(backupPath, out error);
    }

    public bool TryRestoreFromBackup(string backupPath, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
        {
            error = "Backup file not found.";
            return false;
        }

        try
        {
            var raw = File.ReadAllText(backupPath);
            var json = SettingsJsonSanitizer.Repair(raw, out _);
            if (JsonSerializer.Deserialize<AppSettings>(json, JsonSerializerConfig.Default) is null)
            {
                error = "Backup file is not valid Stackroot settings.";
                return false;
            }

            File.Copy(backupPath, Path, overwrite: true);
            lock (_cacheSync)
            {
                _cache = null;
            }

            _ = LoadCore(allowRepair: false);
            PersistenceBlocked = false;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private AppSettings LoadCore(bool allowRepair)
    {
        var defaults = SettingsDefaults.CreateDefaultSettings();
        if (!File.Exists(Path))
        {
            lock (_cacheSync)
            {
                _cache = defaults;
                return AppSettingsCopier.Detach(_cache);
            }
        }

        try
        {
            var raw = File.ReadAllText(Path);
            var json = SettingsJsonSanitizer.Repair(raw, out var repaired);
            if (repaired)
            {
                var backupPath = $"{Path}.pre-repair-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.bak";
                File.Copy(Path, backupPath, overwrite: false);
                File.WriteAllText(Path, json);
            }

            var legacyTestDnsEnabled = ReadLegacySitesTestDnsEnabled(json);
            var stored = JsonSerializer.Deserialize<AppSettings>(json, JsonSerializerConfig.Default);
            if (stored is null)
            {
                throw new InvalidDataException($"Settings file is empty or invalid: {Path}");
            }

            var merged = MergeWithDefaults(defaults, stored, legacyTestDnsEnabled);
            merged.SchemaVersion = SettingsDefaults.SchemaVersion;
            lock (_cacheSync)
            {
                _cache = merged;
                PersistenceBlocked = false;
                return AppSettingsCopier.Detach(_cache);
            }
        }
        catch (Exception ex)
        {
            if (allowRepair && SettingsJsonSanitizer.TryPersistRepairs(Path))
            {
                return LoadCore(allowRepair: false);
            }

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
        EnsureCanPersist();
        settings.SchemaVersion = SettingsDefaults.SchemaVersion;
        _jsonStore.Save(Path, settings);
        lock (_cacheSync)
        {
            _cache = AppSettingsCopier.Detach(settings);
        }
    }

    public AppSettings UpdateGeneral(GeneralSettings patch)
    {
        return Mutate(settings =>
        {
            var updated = settings with
            {
                General = settings.General with
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
                    ShellMetricsEnabled = patch.ShellMetricsEnabled ?? settings.General.ShellMetricsEnabled,
                    ShellMetricsCpuRefreshSeconds = patch.ShellMetricsCpuRefreshSeconds
                        ?? settings.General.ShellMetricsCpuRefreshSeconds,
                    TrustSslCaMachineWide = patch.TrustSslCaMachineWide ?? settings.General.TrustSslCaMachineWide,
                    DiagnosticsLogEnabled = patch.DiagnosticsLogEnabled ?? settings.General.DiagnosticsLogEnabled,
                    DownloadCachePath = patch.DownloadCachePath ?? settings.General.DownloadCachePath
                }
            };

            if (!string.IsNullOrWhiteSpace(updated.General.AppDomain))
            {
                updated = updated with
                {
                    Phpmyadmin = updated.Phpmyadmin with { BaseDomain = updated.General.AppDomain! },
                    Phpredisadmin = updated.Phpredisadmin with { BaseDomain = updated.General.AppDomain! }
                };
            }

            return updated;
        });
    }

    public AppSettings UpdateService(ServiceId id, ServicePortSettings patch)
    {
        return Mutate(settings =>
        {
            var current = settings.Services.TryGetValue(id, out var existing)
                ? existing
                : SettingsDefaults.DefaultServices()[id];

            var updated = settings with
            {
                Services = new Dictionary<ServiceId, ServicePortSettings>(settings.Services)
                {
                    [id] = current with
                    {
                        Enabled = patch.Enabled,
                        Host = string.IsNullOrWhiteSpace(patch.Host) ? current.Host : patch.Host,
                        Port = patch.Port == 0 ? current.Port : patch.Port,
                        SslPort = patch.SslPort ?? current.SslPort,
                        SslEnabled = patch.SslEnabled ?? current.SslEnabled,
                        AutoStart = patch.AutoStart,
                        Supervise = patch.Supervise,
                        PackageId = patch.PackageId ?? current.PackageId
                    }
                }
            };

            if (id != ServiceId.Mailpit)
            {
                return updated;
            }

            var mailpitService = updated.Services[ServiceId.Mailpit];
            return updated with
            {
                Mailpit = updated.Mailpit with
                {
                    Enabled = patch.Enabled,
                    SmtpPort = patch.Port == 0 ? updated.Mailpit.SmtpPort : patch.Port,
                    PackageId = patch.PackageId ?? updated.Mailpit.PackageId,
                    AutoStart = patch.AutoStart,
                    Supervise = patch.Supervise
                }
            };
        });
    }

    public AppSettings UpdatePhp(PhpSettings patch)
    {
        return Mutate(settings => settings with
        {
            Php = settings.Php with
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
            }
        });
    }

    public AppSettings UpdateNode(NodeSettings patch)
    {
        return Mutate(settings => settings with
        {
            Node = settings.Node with
            {
                NvmPackageId = patch.NvmPackageId ?? settings.Node.NvmPackageId,
                ActiveVersion = patch.ActiveVersion ?? settings.Node.ActiveVersion,
                NpmRegistry = string.IsNullOrWhiteSpace(patch.NpmRegistry) ? settings.Node.NpmRegistry : patch.NpmRegistry,
                AutoUseNvmrc = patch.AutoUseNvmrc,
                PinnedVersions = patch.PinnedVersions ?? settings.Node.PinnedVersions
            }
        });
    }

    public AppSettings UpdateSites(SiteDefaults patch)
    {
        return Mutate(settings => settings with
        {
            Sites = settings.Sites with
            {
                AutoHosts = patch.AutoHosts
            }
        });
    }

    public AppSettings UpdateDatabases(DatabaseSettings patch)
    {
        return Mutate(settings => settings with
        {
            Databases = settings.Databases with
            {
                Mysql = patch.Mysql ?? settings.Databases.Mysql,
                Mariadb = patch.Mariadb ?? settings.Databases.Mariadb,
                ActiveSqlEngine = patch.ActiveSqlEngine ?? settings.Databases.ActiveSqlEngine
            }
        });
    }

    public AppSettings UpdatePhpMyAdmin(PhpMyAdminSettings patch)
    {
        return Mutate(settings => settings with
        {
            Phpmyadmin = settings.Phpmyadmin with
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
            }
        });
    }

    public AppSettings UpdatePhpRedisAdmin(PhpRedisAdminSettings patch)
    {
        return Mutate(settings => settings with
        {
            Phpredisadmin = settings.Phpredisadmin with
            {
                Enabled = patch.Enabled,
                BaseDomain = string.IsNullOrWhiteSpace(patch.BaseDomain) ? settings.Phpredisadmin.BaseDomain : patch.BaseDomain,
                AccessMode = patch.AccessMode,
                Subdomain = string.IsNullOrWhiteSpace(patch.Subdomain) ? settings.Phpredisadmin.Subdomain : patch.Subdomain,
                Path = string.IsNullOrWhiteSpace(patch.Path) ? settings.Phpredisadmin.Path : patch.Path,
                PackageId = string.IsNullOrWhiteSpace(patch.PackageId) ? settings.Phpredisadmin.PackageId : patch.PackageId,
                PhpVersionId = patch.PhpVersionId ?? settings.Phpredisadmin.PhpVersionId
            }
        });
    }

    public AppSettings UpdateMailpit(MailpitSettings patch)
    {
        return Mutate(settings =>
        {
            var updated = settings with
            {
                Mailpit = settings.Mailpit with
                {
                    Enabled = patch.Enabled,
                    SmtpPort = patch.SmtpPort <= 0 ? settings.Mailpit.SmtpPort : patch.SmtpPort,
                    WebPort = patch.WebPort <= 0 ? settings.Mailpit.WebPort : patch.WebPort,
                    PackageId = string.IsNullOrWhiteSpace(patch.PackageId) ? settings.Mailpit.PackageId : patch.PackageId,
                    AutoStart = patch.AutoStart,
                    Supervise = patch.Supervise
                }
            };

            if (!updated.Services.TryGetValue(ServiceId.Mailpit, out var mailpitService))
            {
                return updated;
            }

            var services = new Dictionary<ServiceId, ServicePortSettings>(updated.Services)
            {
                [ServiceId.Mailpit] = mailpitService with
                {
                    Enabled = updated.Mailpit.Enabled,
                    Port = updated.Mailpit.SmtpPort,
                    PackageId = updated.Mailpit.PackageId,
                    AutoStart = updated.Mailpit.AutoStart,
                    Supervise = updated.Mailpit.Supervise
                }
            };

            return updated with { Services = services };
        });
    }

    public AppSettings UpdateTestDns(TestDnsSettings patch)
    {
        return Mutate(settings =>
        {
            var updated = settings with
            {
                TestDns = settings.TestDns with
                {
                    Enabled = patch.Enabled,
                    AutoStart = patch.AutoStart,
                    LogRequests = patch.LogRequests,
                    AllowDangerousSettings = settings.TestDns.AllowDangerousSettings,
                    ResolveAddress = LocalDnsResolveAddress.Normalize(patch.ResolveAddress),
                    Suffixes = patch.Suffixes is { Count: > 0 }
                        ? patch.Suffixes.ToList()
                        : settings.TestDns.Suffixes
                }
            };

            if (!updated.Services.TryGetValue(ServiceId.TestDns, out var testDnsService))
            {
                return updated;
            }

            var services = new Dictionary<ServiceId, ServicePortSettings>(updated.Services)
            {
                [ServiceId.TestDns] = testDnsService with
                {
                    Enabled = updated.TestDns.Enabled,
                    Port = 53,
                    AutoStart = updated.TestDns.AutoStart
                }
            };

            return updated with { Services = services };
        });
    }

    public AppSettings UpdateNginxHttp(NginxHttpSettings patch)
    {
        return Mutate(settings => settings with
        {
            NginxHttp = NginxHttpSettingsSanitizer.Sanitize(patch)
        });
    }

    private AppSettings Mutate(Func<AppSettings, AppSettings> mutator)
    {
        EnsureCanPersist();
        lock (_cacheSync)
        {
            if (_cache is null)
            {
                _ = LoadCore(allowRepair: true);
            }

            var updated = mutator(_cache!);
            updated.SchemaVersion = SettingsDefaults.SchemaVersion;
            _jsonStore.Save(Path, updated);
            _cache = AppSettingsCopier.Detach(updated);
            return AppSettingsCopier.Detach(_cache);
        }
    }

    private static AppSettings MergeWithDefaults(AppSettings defaults, AppSettings? stored, bool legacyTestDnsEnabled = false)
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
                ShellMetricsEnabled = stored.General.ShellMetricsEnabled ?? defaults.General.ShellMetricsEnabled,
                ShellMetricsCpuRefreshSeconds = stored.General.ShellMetricsCpuRefreshSeconds
                    ?? defaults.General.ShellMetricsCpuRefreshSeconds,
                TrustSslCaMachineWide = stored.General.TrustSslCaMachineWide ?? defaults.General.TrustSslCaMachineWide,
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
            TestDns = defaults.TestDns with
            {
                Enabled = stored.TestDns?.Enabled ?? legacyTestDnsEnabled,
                AutoStart = stored.TestDns?.AutoStart ?? (stored.TestDns?.Enabled ?? legacyTestDnsEnabled),
                LogRequests = stored.TestDns?.LogRequests ?? defaults.TestDns.LogRequests,
                AllowDangerousSettings = stored.TestDns?.AllowDangerousSettings ?? defaults.TestDns.AllowDangerousSettings,
                ResolveAddress = LocalDnsResolveAddress.Normalize(
                    stored.TestDns?.ResolveAddress ?? defaults.TestDns.ResolveAddress),
                Suffixes = stored.TestDns?.Suffixes is { Count: > 0 } suffixes
                    ? suffixes
                    : defaults.TestDns.Suffixes
            },
            NginxHttp = NginxHttpSettingsSanitizer.Sanitize(stored.NginxHttp),
            Services = MergeServices(defaults.Services, stored.Services)
        };

        merged = merged with
        {
            Services = SyncTestDnsServiceSettings(
                SyncMailpitServiceSettings(merged.Services, merged.Mailpit),
                merged.TestDns)
        };

        merged.SchemaVersion = SettingsDefaults.SchemaVersion;
        return merged;
    }

    private static Dictionary<ServiceId, ServicePortSettings> SyncTestDnsServiceSettings(
        Dictionary<ServiceId, ServicePortSettings> services,
        TestDnsSettings testDns)
    {
        if (!services.TryGetValue(ServiceId.TestDns, out var testDnsService))
        {
            return services;
        }

        services[ServiceId.TestDns] = testDnsService with
        {
            Enabled = testDns.Enabled,
            Port = 53,
            AutoStart = testDns.AutoStart
        };

        return services;
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

    private static bool ReadLegacySitesTestDnsEnabled(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("sites", out var sites)
                || !sites.TryGetProperty("testDnsEnabled", out var flag))
            {
                return false;
            }

            return flag.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
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

    private void EnsureCanPersist()
    {
        if (PersistenceBlocked)
        {
            throw new InvalidOperationException(
                "settings.json could not be read. Restore from a backup before saving changes.");
        }
    }
}
