namespace Stackroot.App.Views.Controls;

public static class PhpSettingHints
{
    public const string ManageIniManually =
        "When enabled, Stackroot stops patching this version's generated php.ini. Edit the file yourself (including advanced OPcache/JIT options), then restart PHP-CGI.";

    public const string MemoryLimit =
        "Maximum memory per script. Use -1 for no limit during local development, or a value like 512M for a fixed cap.";

    public const string MaxExecutionTime =
        "Seconds before PHP stops a script. Use 0 for no limit — helpful for imports, migrations, and long tests.";

    public const string UploadMaxFilesize =
        "Largest single uploaded file PHP accepts. Match or stay below nginx client_max_body_size (512M by default).";

    public const string PostMaxSize =
        "Maximum POST body size. Must be at least upload_max_filesize; 512M matches Stackroot's nginx default.";

    public const string MaxInputTime =
        "Seconds PHP spends parsing input (POST uploads, large forms). Raise for slow uploads.";

    public const string MaxInputVars =
        "Maximum input variables per request. Increase for large WordPress or Laravel admin forms.";

    public const string DefaultSocketTimeout =
        "Seconds before socket-based operations (HTTP clients, streams) time out.";

    public const string RealpathCacheSize =
        "Cache size for resolved file paths. Larger values speed up frameworks that hit many files (Laravel, Symfony).";

    public const string RealpathCacheTtl =
        "Seconds cached realpaths stay valid. Lower values pick up moved files sooner; higher values are faster.";

    public const string OpcacheEnabled =
        "Caches compiled PHP bytecode. Strongly recommended — keeps page loads and artisan commands fast.";

    public const string OpcacheEnableCli =
        "Also use OPcache for CLI (artisan, phpunit, composer scripts). Recommended for local development.";

    public const string OpcacheValidateTimestamps =
        "When on, PHP checks whether source files changed. Keep on during development so edits apply immediately.";

    public const string OpcacheRevalidateFreq =
        "Seconds between timestamp checks when validate_timestamps is on. 0 checks every request — best for dev.";

    public const string OpcacheMemoryConsumption =
        "Megabytes of memory for the OPcache shared store. 256 is a good default for several projects.";

    public const string OpcacheMaxAcceleratedFiles =
        "Maximum PHP files OPcache can store. Raise when you run many packages or a large monorepo.";
}
