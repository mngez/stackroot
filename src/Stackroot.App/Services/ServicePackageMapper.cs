using Stackroot.Core.Abstractions;

namespace Stackroot.App.Services;

public static class ServicePackageMapper
{
    public static PackageType ToPackageType(ServiceId serviceId)
    {
        return serviceId switch
        {
            ServiceId.Nginx => PackageType.Nginx,
            ServiceId.Redis => PackageType.Redis,
            ServiceId.Memcached => PackageType.Memcached,
            ServiceId.Imagemagick => PackageType.Imagemagick,
            ServiceId.Gdlibs => PackageType.Gdlibs,
            ServiceId.Mysql => PackageType.Mysql,
            ServiceId.Mariadb => PackageType.Mariadb,
            ServiceId.Postgresql => PackageType.Postgresql,
            ServiceId.Mongodb => PackageType.Mongodb,
            ServiceId.Mailpit => PackageType.Mailpit,
            _ => throw new ArgumentOutOfRangeException(nameof(serviceId), serviceId, "Unsupported service id.")
        };
    }
}
