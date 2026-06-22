namespace Stackroot.App.Views.Controls;

public static class NginxHttpSettingHints
{
    public const string ManageMainConfigManually =
        "When enabled, Stackroot stops rewriting the main nginx.conf file. Edit it yourself; site vhosts in sites-enabled are still managed.";

    public const string WorkerProcesses =
        "How many nginx worker processes run. Use auto to match CPU cores, or a fixed count (1–128) for predictable memory use.";

    public const string WorkerConnections =
        "Maximum simultaneous connections per worker. Raise for many parallel tabs or dev tools; each connection uses memory.";

    public const string MultiAccept =
        "Lets a worker accept multiple new connections at once. Usually leave on for better throughput on Windows.";

    public const string KeepaliveTimeout =
        "Seconds to keep idle client connections open. Higher values reuse connections; lower values free resources faster.";

    public const string ClientMaxBodySize =
        "Largest request body nginx accepts (uploads). Applies globally unless a site vhost sets a lower limit.";

    public const string TypesHashMaxSize =
        "Hash table size for MIME types. Increase only if nginx warns about types_hash during config test.";

    public const string ServerNamesHashBucketSize =
        "Hash bucket size for server_name entries. Increase when you host many domains or long wildcard names.";

    public const string Sendfile =
        "Uses efficient kernel file sending for static files. Recommended on; turn off only if you see odd static file issues.";

    public const string TcpNopush =
        "Sends HTTP response headers and file data in fewer packets when sendfile is on. Pairs with sendfile for efficiency.";

    public const string GzipEnabled =
        "Compresses text responses (CSS, JS, JSON, SVG). Helpful for local dev and closer to production behavior.";

    public const string GzipCompLevel =
        "Compression effort from 1 (fast) to 9 (smallest). Level 5 is a good balance for development.";

    public const string GzipMinLength =
        "Minimum response size in bytes before gzip applies. Very small files are skipped.";

    public const string AccessLogEnabled =
        "Writes request lines to nginx-access.log. Disable to reduce disk I/O when you do not need access logs.";

    public const string ErrorLogLevel =
        "Verbosity for nginx-error.log: warn is typical; use info/notice for troubleshooting, error for quieter logs.";

    public const string FastCgiConnectTimeout =
        "Seconds to wait when connecting nginx to a PHP FastCGI listener before failing the request.";

    public const string FastCgiSendTimeout =
        "Seconds nginx waits while sending a request to PHP. Raise for slow uploads or long-running scripts.";

    public const string FastCgiReadTimeout =
        "Seconds nginx waits for PHP output. Raise for imports, reports, or debugging breakpoints.";

    public const string ProxyConnectTimeout =
        "Seconds to connect to a proxied dev server (e.g. Vite). Applies to site dev proxy locations.";

    public const string ProxySendTimeout =
        "Seconds nginx waits while sending data to a proxied dev server.";

    public const string ProxyReadTimeout =
        "Seconds nginx waits for responses from a proxied dev server (HMR, API, etc.).";
}
