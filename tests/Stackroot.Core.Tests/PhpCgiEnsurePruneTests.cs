using Stackroot.Core.Services;
using Xunit;

namespace Stackroot.Core.Tests;

public sealed class PhpCgiEnsurePruneTests
{
    [Fact]
    public void ResolveStaleManagedKeys_prunes_absent_only_when_requested()
    {
        var managed = new[] { "php-8.3.32", "php-8.4.23", "php-8.5.0" };
        var ensureOne = new[] { "php-8.4.23" };

        Assert.Empty(PhpCgiRuntime.ResolveStaleManagedKeys(managed, ensureOne, pruneAbsentVersions: false));

        var stale = PhpCgiRuntime.ResolveStaleManagedKeys(managed, ensureOne, pruneAbsentVersions: true);
        Assert.Equal(2, stale.Count);
        Assert.Contains("php-8.3.32", stale);
        Assert.Contains("php-8.5.0", stale);
        Assert.DoesNotContain("php-8.4.23", stale);
    }

    [Fact]
    public void ResolveStaleManagedKeys_full_required_set_keeps_all_listed()
    {
        var managed = new[] { "php-8.3.32", "php-8.4.23", "php-8.5.0" };
        var required = new[] { "php-8.3.32", "php-8.4.23" };

        var stale = PhpCgiRuntime.ResolveStaleManagedKeys(managed, required, pruneAbsentVersions: true);
        Assert.Equal(["php-8.5.0"], stale);
    }
}
