using Stackroot.Core.AdminTools;
using Xunit;

namespace Stackroot.Core.Tests;

public sealed class AdminerEntryTests
{
    [Fact]
    public void Entry_script_signs_in_with_saved_credentials()
    {
        var script = AdminerManager.BuildEntryScript(
        [
            new AdminerLoginServer("mysql", "MySQL", "server", "127.0.0.1:3306", "root", "p'a\\ss")
        ]);

        Assert.Contains("Stackroot Adminer entry", script);
        Assert.Contains("'127.0.0.1:3306'", script);
        Assert.Contains("'p\\'a\\\\ss'", script);
        Assert.Contains("return true;", script);
        Assert.Contains("require __DIR__ . '/adminer.php';", script);
        Assert.DoesNotContain("name='auth[password]'", script);
    }

    [Fact]
    public void Entry_script_explains_a_missing_database_server()
    {
        var script = AdminerManager.BuildEntryScript([]);

        Assert.Contains("$stackrootServers = array(", script);
        Assert.Contains("Settings, under Databases", script);
    }

    [Fact]
    public void Entry_script_points_a_missing_php_extension_at_the_php_page()
    {
        var script = AdminerManager.BuildEntryScript(
        [
            new AdminerLoginServer("postgresql", "PostgreSQL", "pgsql", "127.0.0.1:5432", "postgres", "")
        ]);

        Assert.Contains("then Extensions", script);
        Assert.Contains("ثم الامتدادات", script);
        Assert.Contains("'pgsql'", script);
        Assert.Contains("'pdo_pgsql'", script);
        Assert.DoesNotContain("None of the supported PHP extensions", script);
    }
}
