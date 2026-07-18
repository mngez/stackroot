using Stackroot.Core.IO;
using Xunit;

namespace Stackroot.Core.Tests;

public sealed class StackrootPathResolverTests
{
    [Fact]
    public void Resolve_keeps_data_under_roaming_and_runtime_under_local()
    {
        var paths = StackrootPathResolver.Resolve(ensureDirectories: false);

        var expectedData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Stackroot");
        var expectedRuntime = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Stackroot",
            "runtime");

        Assert.Equal(expectedData, paths.DataRoot);
        Assert.Equal(expectedRuntime, paths.RuntimeRoot);
        Assert.Equal(Path.Combine(expectedData, "logs"), paths.LogsRoot);
        Assert.Equal(Path.Combine(expectedData, "config"), paths.ConfigRoot);
        Assert.NotEqual(paths.DataRoot, Path.GetDirectoryName(paths.RuntimeRoot));
    }

    [Fact]
    public void Resolve_honors_explicit_overrides()
    {
        var root = Path.Combine(Path.GetTempPath(), "stackroot-tests", Guid.NewGuid().ToString("N"));
        var paths = StackrootPathResolver.Resolve(
            new Stackroot.Core.Abstractions.StackrootPaths
            {
                DataRoot = root,
                RuntimeRoot = Path.Combine(root, "custom-runtime")
            },
            ensureDirectories: false);

        Assert.Equal(root, paths.DataRoot);
        Assert.Equal(Path.Combine(root, "custom-runtime"), paths.RuntimeRoot);
    }
}
