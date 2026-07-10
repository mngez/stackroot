using System.Diagnostics;
using Stackroot.Core.Sites.Commands;
using Xunit;

namespace Stackroot.Core.Tests.Sites;

public sealed class SiteCommandRunRegistryTests
{
    [Fact]
    public void GetActiveForSite_finds_running_command_registered_for_that_site()
    {
        var registry = new SiteCommandRunRegistry();
        using var process = StartSleeperProcess();

        registry.Register("C:\\fake\\log.log", process, siteId: "site-1", SiteCommandKind.Custom, "backup-db", "php artisan backup:run");

        var active = registry.GetActiveForSite("site-1");

        Assert.Single(active);
        Assert.Equal("backup-db", active[0].CommandKey);
        Assert.Equal(SiteCommandKind.Custom, active[0].Kind);
        Assert.Equal("php artisan backup:run", active[0].CommandLine);
        Assert.True(registry.IsRunning("C:\\fake\\log.log"));

        registry.TryCancel("C:\\fake\\log.log");
        registry.Complete("C:\\fake\\log.log", -1);
    }

    [Fact]
    public void GetActiveForSite_ignores_commands_registered_for_other_sites()
    {
        var registry = new SiteCommandRunRegistry();
        using var process = StartSleeperProcess();

        registry.Register("C:\\fake\\other.log", process, siteId: "site-2", SiteCommandKind.QuickAction, "migrate", "php artisan migrate");

        Assert.Empty(registry.GetActiveForSite("site-1"));
        Assert.Single(registry.GetActiveForSite("site-2"));

        registry.TryCancel("C:\\fake\\other.log");
        registry.Complete("C:\\fake\\other.log", -1);
    }

    [Fact]
    public void Complete_removes_entry_and_raises_CommandCompleted_with_exit_code()
    {
        var registry = new SiteCommandRunRegistry();
        using var process = StartSleeperProcess();

        registry.Register("C:\\fake\\log.log", process, siteId: "site-1", SiteCommandKind.Custom, "backup-db", "php artisan backup:run");

        SiteCommandCompletedEventArgs? received = null;
        registry.CommandCompleted += (_, args) => received = args;

        registry.Complete("C:\\fake\\log.log", exitCode: 0);

        Assert.NotNull(received);
        Assert.Equal("site-1", received!.SiteId);
        Assert.Equal("backup-db", received.CommandKey);
        Assert.Equal(SiteCommandKind.Custom, received.Kind);
        Assert.Equal(0, received.ExitCode);
        Assert.False(registry.IsRunning("C:\\fake\\log.log"));
        Assert.Empty(registry.GetActiveForSite("site-1"));

        TryKill(process);
    }

    [Fact]
    public void TryCancel_kills_process_and_reconciliation_can_still_stop_it_after_registration()
    {
        // Simulates the bug scenario: a command is registered (as if started from a site page),
        // then the caller only knows about it via GetActiveForSite (as if reconciled after
        // navigating back to the page), and must still be able to cancel it using just the log path.
        var registry = new SiteCommandRunRegistry();
        var process = StartSleeperProcess();

        registry.Register("C:\\fake\\log.log", process, siteId: "site-1", SiteCommandKind.QuickAction, "migrate", "php artisan migrate");

        var rediscovered = Assert.Single(registry.GetActiveForSite("site-1"));

        var cancelled = registry.TryCancel(rediscovered.LogPath);

        Assert.True(cancelled);
        Assert.True(process.HasExited);

        registry.Complete(rediscovered.LogPath, -1);
    }

    private static Process StartSleeperProcess()
    {
        var startInfo = new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 > nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start test process.");
        return process;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
        finally
        {
            process.Dispose();
        }
    }
}
