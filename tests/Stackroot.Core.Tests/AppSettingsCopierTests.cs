using Stackroot.Core.Abstractions;
using Stackroot.Core.Settings;
using Xunit;

namespace Stackroot.Core.Tests;

public sealed class AppSettingsCopierTests
{
    [Fact]
    public void Detach_creates_independent_service_dictionary()
    {
        var source = SettingsDefaults.CreateDefaultSettings();
        source.Services[ServiceId.Nginx] = source.Services[ServiceId.Nginx] with { Port = 8088 };

        var copy = AppSettingsCopier.Detach(source);
        copy.Services[ServiceId.Nginx] = copy.Services[ServiceId.Nginx] with { Port = 9090 };

        Assert.Equal(8088, source.Services[ServiceId.Nginx].Port);
        Assert.Equal(9090, copy.Services[ServiceId.Nginx].Port);
    }
}
