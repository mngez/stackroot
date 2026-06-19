namespace Stackroot.Core.Abstractions;

public enum PackageType
{
    Php, Nginx, Redis, Memcached, Imagemagick, Gdlibs, Mysql, Mariadb, Postgresql, Mongodb,
    Nvm, Node, Phpmyadmin, Phpredisadmin, Composer, Pnpm, Vite, Git, Openssl, Python, Sqlite, Notepadpp, Mailpit, Laravel, WpCli, Mongosh, MongodbTools
}

public enum PlatformType
{
    WinX64,
    WinArm64
}

public enum PackageSourceType
{
    Bundled,
    Remote
}

public enum ServiceStatus
{
    Stopped,
    Starting,
    Running,
    Error
}

public enum ServiceStartMode
{
    /// <summary>Block until the service is listening (and SQL credentials are applied when applicable).</summary>
    WaitUntilReady,

    /// <summary>Return immediately with Starting; finish readiness checks on a background thread.</summary>
    Background
}

public enum InstallPhase
{
    Resolving,
    Downloading,
    Extracting,
    Registering,
    Done,
    Error
}

public enum ServiceId
{
    Nginx, Redis, Memcached, Imagemagick, Gdlibs, Mysql, Mariadb, Postgresql, Mongodb, Mailpit
}

public enum PreferredEditor
{
    System, Vscode, Cursor, NotepadPlusPlus, Custom
}

public enum CloseBehavior
{
    Ask, Quit, Background
}

public enum ServiceRuntime
{
    Process, Library
}

public enum ServiceCategory
{
    Web, Cache, Database, Mail, Optional
}

public enum SiteTemplateId
{
    Static, Laravel, Wordpress
}

public enum SitePathMode
{
    Default, Custom
}

public enum SiteCommandRuntime
{
    Php, Composer, Npm, Node, Python, Shell
}

public enum ProcessStatus
{
    Stopped, Running, Error, Restarting
}

public enum ProcessScopeType
{
    Site, Global
}

public enum SqlEngine
{
    Mysql, Mariadb, Postgresql, Mongodb, Sqlite
}

public enum AccessMode
{
    Subdomain, Path
}

public record class RequiresPhp
{
    public string? Min { get; set; }
    public string? MaxExclusive { get; set; }
}

public record class BundledSource
{
    public PackageSourceType Type { get; init; } = PackageSourceType.Bundled;
    public string Archive { get; init; } = string.Empty;
}

public record class RemoteSource
{
    public PackageSourceType Type { get; init; } = PackageSourceType.Remote;
    public string Url { get; init; } = string.Empty;
    public List<string>? Mirrors { get; init; }
    public string? Sha256 { get; init; }
    public long? Size { get; init; }
}

public record class PackageSource
{
    public PackageSourceType Type { get; init; } = PackageSourceType.Bundled;
    public string? Archive { get; init; }
    public string? Url { get; init; }
    public List<string>? Mirrors { get; init; }
    public string? Sha256 { get; init; }
    public long? Size { get; init; }
}

public record class PackageEntry
{
    public string Id { get; init; } = string.Empty;
    public PackageType Type { get; init; }
    public string Version { get; init; } = string.Empty;
    public PlatformType Platform { get; init; } = PlatformType.WinX64;
    public string Label { get; init; } = string.Empty;
    public string? Description { get; init; }
    public PackageSource Source { get; init; } = new();
    public RemoteSource? Remote { get; init; }
    public string InstallDir { get; init; } = string.Empty;
    public string? Executable { get; init; }
    public bool? Enabled { get; init; }
    public RequiresPhp? RequiresPhp { get; init; }
}

public record class PackageCatalog
{
    public int SchemaVersion { get; init; }
    public string UpdatedAt { get; init; } = string.Empty;
    public List<PackageEntry> Packages { get; init; } = [];
}

public record class InstalledPackage
{
    public string Id { get; init; } = string.Empty;
    public PackageType Type { get; init; }
    public string Version { get; init; } = string.Empty;
    public string InstalledAt { get; init; } = string.Empty;
    public string InstallPath { get; init; } = string.Empty;
    public PackageSourceType Source { get; init; }
}

public record class InstallRegistry
{
    public int SchemaVersion { get; init; }
    public List<InstalledPackage> Packages { get; init; } = [];
}

public record class StackrootPaths
{
    public string DataRoot { get; init; } = string.Empty;
    public string RuntimeRoot { get; init; } = string.Empty;
    public string ResourcesRoot { get; init; } = string.Empty;
    public string SitesRoot { get; init; } = string.Empty;
    public string ConfigRoot { get; init; } = string.Empty;
    public string LogsRoot { get; init; } = string.Empty;
}

public record class ServiceInfo
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public ServiceStatus Status { get; init; }
    public int? Pid { get; init; }
    public int? Port { get; init; }
    public int? SslPort { get; init; }
    public bool? PortOpen { get; init; }
    public bool? Installed { get; init; }
    public bool? Enabled { get; init; }
    public string? Message { get; init; }
}

public record class InstallProgress
{
    public string PackageId { get; init; } = string.Empty;
    public InstallPhase Phase { get; init; }
    public int Percent { get; init; }
    public string Message { get; init; } = string.Empty;
}

public record class ServicePortSettings
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; }
    public int? SslPort { get; set; }
    public bool? SslEnabled { get; set; }
    public bool AutoStart { get; set; }
    public bool Supervise { get; set; }
    public string? PackageId { get; set; }
    public string? PhpVersionId { get; set; }
}

public record class GeneralSettings
{
    public string? WwwPath { get; set; }
    public string? AppDomain { get; set; }
    public PreferredEditor? PreferredEditor { get; set; }
    public string? CustomEditorPath { get; set; }
    public CloseBehavior? CloseBehavior { get; set; }
    public int? LogRetentionDays { get; set; }
    public bool? AddBinToPath { get; set; }
    public bool? DiagnosticsLogEnabled { get; set; }
    public bool? ThumbnailsEnabled { get; set; }
    public bool? LaunchAtStartup { get; set; }
    public string? DownloadCachePath { get; set; }
}

public record class DownloadCacheEntry
{
    public string PackageId { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string? Url { get; init; }
    public long SizeBytes { get; init; }
    public string? Sha256 { get; init; }
    public string DownloadedAt { get; init; } = string.Empty;
}

public record class DownloadCacheRegistry
{
    public int SchemaVersion { get; init; } = 1;
    public List<DownloadCacheEntry> Entries { get; init; } = [];
}

public record class PhpVersionSettings
{
    public string MemoryLimit { get; set; } = "512M";
    public string MaxExecutionTime { get; set; } = "120";
    public string UploadMaxFilesize { get; set; } = "64M";
    public string PostMaxSize { get; set; } = "64M";
    public bool? DisplayErrors { get; set; }
    public bool? HideWarnings { get; set; }
    public bool? HideDeprecated { get; set; }
    public bool? LogErrors { get; set; }
    public Dictionary<string, bool> Extensions { get; set; } = [];
    public Dictionary<string, string> IniOverrides { get; set; } = [];
}

public record class PhpSettings
{
    public string? ActiveVersionId { get; set; }
    public int FpmPort { get; set; } = 9000;
    public string FpmHost { get; set; } = "127.0.0.1";
    public Dictionary<string, PhpVersionSettings>? Versions { get; set; }
    public string? MemoryLimit { get; set; }
    public string? MaxExecutionTime { get; set; }
    public string? UploadMaxFilesize { get; set; }
    public string? PostMaxSize { get; set; }
    public Dictionary<string, bool>? Extensions { get; set; }
    public Dictionary<string, string>? IniOverrides { get; set; }
}

public record class NodeSettings
{
    public string? NvmPackageId { get; set; }
    public string? ActiveVersion { get; set; }
    public string NpmRegistry { get; set; } = "https://registry.npmjs.org/";
    public bool AutoUseNvmrc { get; set; } = true;
    public List<string>? PinnedVersions { get; set; }
}

public record class SiteDefaults
{
    public bool AutoHosts { get; set; } = true;

    /// <summary>
    /// Route <c>.test</c> DNS queries to Stackroot's local resolver (127.0.0.1:53 via NRPT).
    /// Disabled by default; does not affect other domains.
    /// </summary>
    public bool TestDnsEnabled { get; set; }
}

public record class DatabaseCredentials
{
    public string Username { get; set; } = "root";
    public string Password { get; set; } = "root";
}

public record class DatabaseSettings
{
    public DatabaseCredentials Mysql { get; set; } = new();
    public DatabaseCredentials Mariadb { get; set; } = new();
    public DatabaseCredentials Postgresql { get; set; } = new() { Username = "postgres", Password = "" };
    public DatabaseCredentials Mongodb { get; set; } = new() { Username = "", Password = "" };
    public SqlEngine? ActiveSqlEngine { get; set; }
}

public record class PhpMyAdminSettings
{
    public bool Enabled { get; set; } = true;
    public string BaseDomain { get; set; } = "stackroot.test";
    public AccessMode AccessMode { get; set; } = AccessMode.Path;
    public string Subdomain { get; set; } = "phpmyadmin";
    public string Path { get; set; } = "phpmyadmin";
    public string PackageId { get; set; } = string.Empty;
    public string? PhpVersionId { get; set; }
    public string? BlowfishSecret { get; set; }
    public string? Domain { get; set; }
}

public record class PhpRedisAdminSettings
{
    public bool Enabled { get; set; } = true;
    public string BaseDomain { get; set; } = "stackroot.test";
    public AccessMode AccessMode { get; set; } = AccessMode.Path;
    public string Subdomain { get; set; } = "phpredisadmin";
    public string Path { get; set; } = "phpredisadmin";
    public string PackageId { get; set; } = string.Empty;
    public string? PhpVersionId { get; set; }
}

public record class MailpitSettings
{
    public bool Enabled { get; set; } = true;
    public int SmtpPort { get; set; } = 1025;
    public int WebPort { get; set; } = 8025;
    public string PackageId { get; set; } = string.Empty;
    public bool AutoStart { get; set; } = true;
    public bool Supervise { get; set; } = true;
}

public record class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public GeneralSettings General { get; set; } = new();
    public PhpSettings Php { get; set; } = new();
    public NodeSettings Node { get; set; } = new();
    public SiteDefaults Sites { get; set; } = new();
    public DatabaseSettings Databases { get; set; } = new();
    public PhpMyAdminSettings Phpmyadmin { get; set; } = new();
    public PhpRedisAdminSettings Phpredisadmin { get; set; } = new();
    public MailpitSettings Mailpit { get; set; } = new();
    public Dictionary<ServiceId, ServicePortSettings> Services { get; set; } = [];
}

public record class ServiceDefinition
{
    public ServiceId Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public ServiceCategory Category { get; init; }
    public string Description { get; init; } = string.Empty;
    public int DefaultPort { get; init; }
    public int? DefaultSslPort { get; init; }
    public string? PackageId { get; init; }
    public string? Executable { get; init; }
    public ServiceRuntime? Runtime { get; init; }
}

public record class SiteDevProxy
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string LocationPath { get; set; } = "/";
    public string TargetUrl { get; set; } = string.Empty;
    public bool? Websocket { get; set; }
}

public record class SiteDevProcess
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public SiteCommandRuntime Runtime { get; set; } = SiteCommandRuntime.Shell;
    public List<string> Argv { get; set; } = [];
    public string? Cwd { get; set; }
    public bool Enabled { get; set; } = true;
    public string? FromPreset { get; set; }
    public string? PhpVersionId { get; set; }
    public string? NodeVersion { get; set; }
}

public record class Site
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public SiteTemplateId Template { get; set; } = SiteTemplateId.Static;
    public string? PhpVersionId { get; set; }
    public string Path { get; set; } = string.Empty;
    public string DocumentRoot { get; set; } = ".";
    public SitePathMode? PathMode { get; set; }
    public bool Enabled { get; set; } = true;
    public bool? Featured { get; set; }
    public bool? ForceHttps { get; set; }
    public List<SiteDevProxy>? DevProxies { get; set; }
    public List<SiteDevProcess>? DevProcesses { get; set; }
    public string CreatedAt { get; set; } = string.Empty;
    public string UpdatedAt { get; set; } = string.Empty;
}

public record class SitesRegistry
{
    public int SchemaVersion { get; set; } = 1;
    public List<Site> Sites { get; set; } = [];
}

public record class CreateSiteInput
{
    public string Name { get; init; } = string.Empty;
    public string Domain { get; init; } = string.Empty;
    public string? DomainSuffix { get; init; }
    public SiteTemplateId Template { get; init; } = SiteTemplateId.Static;
    public string? PhpVersionId { get; init; }
    public SitePathMode? PathMode { get; init; }
    public string? CustomPath { get; init; }
}

public record class UpdateSiteInput
{
    public string? Name { get; init; }
    public SiteTemplateId? Template { get; init; }
    public string? PhpVersionId { get; init; }
    public string? DocumentRoot { get; init; }
    public bool? Enabled { get; init; }
    public bool? Featured { get; init; }
    public bool? ForceHttps { get; init; }
    public List<SiteDevProxy>? DevProxies { get; init; }
}

public record class SupervisedProcessDefinition
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public SiteCommandRuntime Runtime { get; init; } = SiteCommandRuntime.Shell;
    public List<string> Argv { get; init; } = [];
    public string? Cwd { get; init; }
    public bool Enabled { get; init; } = true;
    public bool? AutoStart { get; init; }
    public bool? Featured { get; init; }
    public string? FromPreset { get; init; }
    public string? PhpVersionId { get; init; }
    public string? NodeVersion { get; init; }
    /// <summary>Seconds to wait before restarting after exit. Null uses supervisor default (2s, then backoff).</summary>
    public int? RestartDelaySeconds { get; init; }
}

public record class GlobalProcess : SupervisedProcessDefinition
{
    public string? SiteId { get; init; }
    public string WorkDir { get; init; } = string.Empty;
    public new bool AutoStart { get; init; }
}

public record class GlobalProcessesRegistry
{
    public int SchemaVersion { get; init; } = 1;
    public List<GlobalProcess> Processes { get; init; } = [];
}

public record class ProcessLog
{
    public string Content { get; init; } = string.Empty;
    public bool Running { get; init; }
    public int? Pid { get; init; }
    public string? CommandLine { get; init; }
}

public record class ProcessInfo
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public SiteCommandRuntime Runtime { get; init; } = SiteCommandRuntime.Shell;
    public string RuntimeLabel { get; init; } = string.Empty;
    public List<string> Argv { get; init; } = [];
    public string Cwd { get; init; } = string.Empty;
    public string? WorkDir { get; init; }
    public string? ResolvedCwd { get; init; }
    public bool Enabled { get; init; }
    public bool AutoStart { get; init; }
    public bool? Featured { get; init; }
    public bool Available { get; init; }
    public ProcessStatus Status { get; init; } = ProcessStatus.Stopped;
    public string CommandLine { get; init; } = string.Empty;
    public string? PhpVersionId { get; init; }
    public string? NodeVersion { get; init; }
    public int? RestartDelaySeconds { get; init; }
    public int? Pid { get; init; }
    public string? Message { get; init; }
    public string? FromPreset { get; init; }
    public bool? HasLog { get; init; }
    public bool? Supervised { get; init; }
    public ProcessScopeType Scope { get; init; } = ProcessScopeType.Global;
    public string? SiteId { get; init; }
    public string? SiteName { get; init; }
    public string? SiteDomain { get; init; }
}

public record class ProcessScope
{
    public ProcessScopeType Type { get; init; } = ProcessScopeType.Global;
    public string? SiteId { get; init; }
    public string ProcessId { get; init; } = string.Empty;
}
