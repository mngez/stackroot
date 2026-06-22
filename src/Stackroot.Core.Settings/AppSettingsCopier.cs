using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Settings;

public static class AppSettingsCopier
{
    public static AppSettings Detach(AppSettings source)
    {
        return source with
        {
            General = source.General with { },
            Php = CopyPhp(source.Php),
            Node = source.Node with
            {
                PinnedVersions = source.Node.PinnedVersions is null
                    ? null
                    : [.. source.Node.PinnedVersions]
            },
            Sites = source.Sites with { },
            Databases = source.Databases with
            {
                Mysql = source.Databases.Mysql with { },
                Mariadb = source.Databases.Mariadb with { },
                Postgresql = source.Databases.Postgresql with { },
                Mongodb = source.Databases.Mongodb with { }
            },
            Phpmyadmin = source.Phpmyadmin with { },
            Phpredisadmin = source.Phpredisadmin with { },
            Mailpit = source.Mailpit with { },
            TestDns = source.TestDns with { },
            NginxHttp = source.NginxHttp with { },
            Services = CopyServices(source.Services)
        };
    }

    private static PhpSettings CopyPhp(PhpSettings source)
    {
        Dictionary<string, PhpVersionSettings>? versions = null;
        if (source.Versions is not null)
        {
            versions = new Dictionary<string, PhpVersionSettings>(source.Versions.Count);
            foreach (var (versionId, versionSettings) in source.Versions)
            {
                versions[versionId] = versionSettings with
                {
                    Extensions = versionSettings.Extensions is null
                        ? []
                        : new Dictionary<string, bool>(versionSettings.Extensions),
                    IniOverrides = versionSettings.IniOverrides is null
                        ? []
                        : new Dictionary<string, string>(versionSettings.IniOverrides)
                };
            }
        }

        return source with
        {
            Versions = versions,
            Extensions = source.Extensions is null ? null : new Dictionary<string, bool>(source.Extensions),
            IniOverrides = source.IniOverrides is null ? null : new Dictionary<string, string>(source.IniOverrides)
        };
    }

    private static Dictionary<ServiceId, ServicePortSettings> CopyServices(
        Dictionary<ServiceId, ServicePortSettings> source)
    {
        var copy = new Dictionary<ServiceId, ServicePortSettings>(source.Count);
        foreach (var (serviceId, settings) in source)
        {
            copy[serviceId] = settings with { };
        }

        return copy;
    }
}
