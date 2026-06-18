using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Sites.Installers;

public enum InstallerMessageKind { Info, Progress, Success, Warning, Error }

public sealed class InstallerMessage
{
    public InstallerMessageKind Kind { get; init; }
    public string Text { get; init; } = string.Empty;
}

public sealed class SiteInstallOptions
{
    /// <summary>Create a database for the site.</summary>
    public bool CreateDatabase { get; init; }

    /// <summary>Database name override — auto-generated from domain if empty.</summary>
    public string? DatabaseName { get; init; }

    /// <summary>WordPress-specific settings (used by WordPress installer).</summary>
    public WordPressInstallConfig? WordPress { get; init; }

    /// <summary>Laravel-specific settings (used by Laravel installer).</summary>
    public LaravelInstallConfig? Laravel { get; init; }
}

public sealed class WordPressInstallConfig
{
    public string SiteTitle { get; init; } = "";
    public string AdminUser { get; init; } = "admin";
    public string AdminPassword { get; init; } = "";
    public string AdminEmail { get; init; } = "";
    /// <summary>Database engine to use (MySQL or MariaDB).</summary>
    public SqlEngine DatabaseEngine { get; init; } = SqlEngine.Mysql;
}

public sealed class LaravelInstallConfig
{
    /// <summary>Starter kit: none, breeze, or jetstream.</summary>
    public string StarterKit { get; init; } = "none";

    /// <summary>Stack when using a starter kit: inertia, livewire, or api.</summary>
    public string Stack { get; init; } = "inertia";

    /// <summary>Database engine: mysql, mariadb, postgresql, or sqlite.</summary>
    public SqlEngine DatabaseEngine { get; init; } = SqlEngine.Mysql;

    /// <summary>Run npm install + npm run build after Composer.</summary>
    public bool RunNpmBuild { get; init; } = true;

    /// <summary>Run php artisan migrate after setup.</summary>
    public bool RunMigrations { get; init; } = true;
}

public sealed class SiteInstallResult
{
    public bool Success { get; init; }
    public string? SiteUrl { get; init; }
    public string? AdminUrl { get; init; }
    public string? DatabaseName { get; init; }
    public string? AdminUser { get; init; }
    public string? AdminPassword { get; init; }
    public IReadOnlyList<string> PostInstallTips { get; init; } = [];
}

/// <summary>
/// Implemented once per installable project type (Laravel, WordPress, …).
/// Each implementation lives in a single file under Installers/ and is auto-discovered.
/// </summary>
public interface ISiteInstaller
{
    /// <summary>Matches <see cref="Models.SiteTemplateIds"/>.</summary>
    string TemplateId { get; }

    /// <summary>Human label shown in the UI.</summary>
    string DisplayName { get; }

    /// <summary>One-line description shown next to the install checkbox.</summary>
    string Description { get; }

    /// <summary>Emoji or short icon for the post-install card.</summary>
    string Icon { get; }

    /// <summary>Quick check before starting (e.g. folder exists and is empty-ish).</summary>
    bool CanInstall(Models.Site site);

    /// <summary>Runs the install. Reports progress via the channel callback.</summary>
    Task<SiteInstallResult> InstallAsync(
        Models.Site site,
        SiteInstallOptions options,
        Action<InstallerMessage> onMessage,
        CancellationToken cancel);
}

public sealed class StackrootWpCredentials
{
    public string Password { get; set; } = "";
    public string User { get; set; } = "";
    public string Email { get; set; } = "";
}
