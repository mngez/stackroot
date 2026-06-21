using Stackroot.Core.Windows;
using Xunit;

namespace Stackroot.Core.Tests;

public sealed class ProcessImageToolsTests
{
    [Theory]
    [InlineData(@"C:\laragon\bin\mysql\mysql-8.4.4-winx64\bin\mysqld.exe", @"C:\stackroot\runtime\packages\mysql-8.4.4", false)]
    [InlineData(@"C:\stackroot\runtime\packages\mysql-8.4.4\bin\mysqld.exe", @"C:\stackroot\runtime\packages\mysql-8.4.4", true)]
    [InlineData(@"C:\stackroot\runtime\packages\nginx-1.26.2\nginx.exe", @"C:\stackroot\runtime\packages\nginx-1.26.2", true)]
    [InlineData(@"C:\laragon\bin\memcached\memcached-1.6.8\memcached.exe", @"C:\stackroot\runtime\packages\memcached-1.6.8", false)]
    public void ExecutablePathIsUnderInstallPath_requires_stackroot_install_prefix(
        string executablePath,
        string installPath,
        bool expected)
    {
        Assert.Equal(expected, ProcessImageTools.ExecutablePathIsUnderInstallPath(executablePath, installPath));
    }

    [Fact]
    public void ExecutablePathReferencesInstallFolder_can_weak_match_laragon_memcached_folder_name()
    {
        Assert.True(ProcessImageTools.ExecutablePathReferencesInstallFolder(
            @"C:\laragon\bin\memcached\memcached-1.6.8\memcached.exe",
            @"C:\stackroot\runtime\packages\memcached-1.6.8"));
    }
}
