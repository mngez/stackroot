using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Settings;

public static class SettingsDefaults
{
    public const string DefaultPhpMyAdminPackageId = "phpmyadmin-5.2.3";
    public const string DefaultPhpRedisAdminPackageId = "phpredisadmin-1.26.0";
    public const string DefaultMailpitPackageId = "mailpit-1.21.8";
    public const int SchemaVersion = 2;

    public static readonly IReadOnlyList<ServiceDefinition> ServiceDefinitions =
    [
        new()
        {
            Id = ServiceId.Nginx,
            Name = "Nginx",
            Category = ServiceCategory.Web,
            Description = "Web server - HTTP/HTTPS reverse proxy",
            DefaultPort = 80,
            DefaultSslPort = 443,
            PackageId = "nginx-1.26.2",
            Executable = "nginx.exe"
        },
        new()
        {
            Id = ServiceId.Redis,
            Name = "Redis",
            Category = ServiceCategory.Cache,
            Description = "In-memory cache and message broker",
            DefaultPort = 6379,
            PackageId = "redis-7.4.2",
            Executable = "redis-server.exe"
        },
        new()
        {
            Id = ServiceId.Memcached,
            Name = "Memcached",
            Category = ServiceCategory.Cache,
            Description = "Distributed memory object cache server",
            DefaultPort = 11211,
            PackageId = "memcached-1.6.8",
            Executable = "memcached.exe"
        },
        new()
        {
            Id = ServiceId.Imagemagick,
            Name = "ImageMagick",
            Category = ServiceCategory.Optional,
            Description = "Image processing libraries for Imagick",
            DefaultPort = 0,
            PackageId = "imagemagick-7.1.2-25",
            Runtime = ServiceRuntime.Library
        },
        new()
        {
            Id = ServiceId.Gdlibs,
            Name = "GD Libraries",
            Category = ServiceCategory.Optional,
            Description = "Graphics libraries for PHP GD extension",
            DefaultPort = 0,
            PackageId = "gd-libs-8.4",
            Runtime = ServiceRuntime.Library
        },
        new()
        {
            Id = ServiceId.Mysql,
            Name = "MySQL",
            Category = ServiceCategory.Database,
            Description = "Relational database server",
            DefaultPort = 3306,
            PackageId = "mysql-8.4.9",
            Executable = "bin/mysqld.exe"
        },
        new()
        {
            Id = ServiceId.Mariadb,
            Name = "MariaDB",
            Category = ServiceCategory.Database,
            Description = "MySQL-compatible database server",
            DefaultPort = 3306,
            PackageId = "mariadb-10.11.18",
            Executable = "bin/mysqld.exe"
        },
        new()
        {
            Id = ServiceId.Postgresql,
            Name = "PostgreSQL",
            Category = ServiceCategory.Database,
            Description = "Advanced open-source database",
            DefaultPort = 5432,
            PackageId = "postgresql-16.14",
            Executable = "bin/postgres.exe"
        },
        new()
        {
            Id = ServiceId.Mongodb,
            Name = "MongoDB",
            Category = ServiceCategory.Database,
            Description = "Document-oriented NoSQL database",
            DefaultPort = 27017,
            PackageId = "mongodb-8.0.8",
            Executable = "bin/mongod.exe"
        },
        new()
        {
            Id = ServiceId.Mailpit,
            Name = "Mailpit",
            Category = ServiceCategory.Mail,
            Description = "Local SMTP catcher and web UI",
            DefaultPort = 1025,
            PackageId = DefaultMailpitPackageId,
            Executable = "mailpit.exe"
        }
    ];

    public static AppSettings CreateDefaultSettings()
    {
        var php83 = DefaultPhpVersionSettings();
        var php84 = DefaultPhpVersionSettings();

        var settings = new AppSettings
        {
            SchemaVersion = 1,
            General = new GeneralSettings
            {
                AppDomain = "stackroot.test",
                CloseBehavior = CloseBehavior.Background,
                LogRetentionDays = 30,
                DiagnosticsLogEnabled = false,
                ThumbnailsEnabled = false,
                LaunchAtStartup = false
            },
            Php = new PhpSettings
            {
                FpmHost = "127.0.0.1",
                FpmPort = 9000,
                ActiveVersionId = "php-8.3.31",
                Versions = new Dictionary<string, PhpVersionSettings>
                {
                    ["php-8.3.31"] = php83,
                    ["php-8.4.22"] = php84
                }
            },
            Node = new NodeSettings
            {
                NpmRegistry = "https://registry.npmjs.org/",
                AutoUseNvmrc = true
            },
            Sites = new SiteDefaults { AutoHosts = true },
            Databases = new DatabaseSettings
            {
                Mysql = new DatabaseCredentials { Username = "root", Password = "root" },
                Mariadb = new DatabaseCredentials { Username = "root", Password = "root" }
            },
            Phpmyadmin = new PhpMyAdminSettings
            {
                Enabled = true,
                BaseDomain = "stackroot.test",
                AccessMode = AccessMode.Path,
                Subdomain = "phpmyadmin",
                Path = "phpmyadmin",
                PackageId = DefaultPhpMyAdminPackageId
            },
            Phpredisadmin = new PhpRedisAdminSettings
            {
                Enabled = true,
                BaseDomain = "stackroot.test",
                AccessMode = AccessMode.Path,
                Subdomain = "phpredisadmin",
                Path = "phpredisadmin",
                PackageId = DefaultPhpRedisAdminPackageId
            },
            Mailpit = new MailpitSettings
            {
                Enabled = true,
                SmtpPort = 1025,
                WebPort = 8025,
                PackageId = DefaultMailpitPackageId,
                AutoStart = true,
                Supervise = true
            },
            Services = DefaultServices()
        };

        settings.Services[ServiceId.Mailpit] = settings.Services[ServiceId.Mailpit] with
        {
            Enabled = settings.Mailpit.Enabled,
            Port = settings.Mailpit.SmtpPort,
            PackageId = settings.Mailpit.PackageId,
            AutoStart = settings.Mailpit.AutoStart,
            Supervise = settings.Mailpit.Supervise
        };

        return settings;
    }

    public static PhpVersionSettings DefaultPhpVersionSettings()
    {
        return new PhpVersionSettings
        {
            MemoryLimit = "256M",
            MaxExecutionTime = "120",
            UploadMaxFilesize = "64M",
            PostMaxSize = "64M",
            DisplayErrors = true,
            HideWarnings = false,
            HideDeprecated = true,
            LogErrors = true,
            Extensions = new Dictionary<string, bool>(),
            IniOverrides = new Dictionary<string, string>()
        };
    }

    public static Dictionary<ServiceId, ServicePortSettings> DefaultServices()
    {
        var services = new Dictionary<ServiceId, ServicePortSettings>();
        foreach (var definition in ServiceDefinitions)
        {
            services[definition.Id] = new ServicePortSettings
            {
                Enabled = definition.Id is ServiceId.Nginx or ServiceId.Redis,
                Host = "127.0.0.1",
                Port = definition.DefaultPort,
                SslPort = definition.DefaultSslPort,
                SslEnabled = definition.Id == ServiceId.Nginx ? true : null,
                AutoStart = false,
                Supervise = definition.Id is ServiceId.Mysql or ServiceId.Redis or ServiceId.Postgresql or ServiceId.Mongodb or ServiceId.Memcached,
                PackageId = definition.PackageId
            };
        }

        return services;
    }
}
