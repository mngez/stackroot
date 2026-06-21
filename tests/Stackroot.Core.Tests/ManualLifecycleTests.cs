using Xunit;

namespace Stackroot.Core.Tests;

public sealed class ManualLifecycleTests
{
    [Fact(Skip = "Manual: tray → quit × 10 — verify zero orphan nginx/mysql/php-cgi/redis in Process Explorer.")]
    public void T4_tray_quit_leaves_no_orphan_processes()
    {
    }
}
