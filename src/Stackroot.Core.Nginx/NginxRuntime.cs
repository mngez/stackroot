using System.Text;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Settings;

namespace Stackroot.Core.Nginx;

public static class NginxRuntime
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static string nginxPrefix(StackrootPaths paths)
    {
        return Path.Combine(paths.ConfigRoot, "nginx");
    }

    public static string SitesEnabledDirectory(StackrootPaths paths) =>
        Path.Combine(nginxPrefix(paths), "conf", "sites-enabled");

    public static string MainConfigPath(StackrootPaths paths) =>
        Path.Combine(nginxPrefix(paths), "conf", "nginx.conf");

    public static void setupNginxRuntime(StackrootPaths paths, string nginxInstallPath)
    {
        var packageRoot = PackageBinaryResolver.ResolvePackageRoot(nginxInstallPath, "nginx.exe");
        var prefix = nginxPrefix(paths);
        var confDir = Path.Combine(prefix, "conf");
        var htmlDir = Path.Combine(prefix, "html");

        Directory.CreateDirectory(confDir);
        Directory.CreateDirectory(htmlDir);
        Directory.CreateDirectory(Path.Combine(prefix, "logs"));
        Directory.CreateDirectory(Path.Combine(prefix, "temp"));

        foreach (var file in new[] { "mime.types", "fastcgi_params", "fastcgi.conf", "scgi_params", "uwsgi_params" })
        {
            var source = Path.Combine(packageRoot, "conf", file);
            var destination = Path.Combine(confDir, file);
            if (File.Exists(source))
            {
                File.Copy(source, destination, overwrite: true);
            }
        }

        var fastCgiParams = Path.Combine(confDir, "fastcgi_params");
        if (!File.Exists(fastCgiParams))
        {
            File.WriteAllText(fastCgiParams, DefaultFastCgiParams, Utf8NoBom);
        }

        var mimeTypes = Path.Combine(confDir, "mime.types");
        if (!File.Exists(mimeTypes))
        {
            File.WriteAllText(mimeTypes, "types { text/html html htm; application/javascript js; text/css css; }\n", Utf8NoBom);
        }

        var indexHtml = Path.Combine(htmlDir, "index.html");
        if (!File.Exists(indexHtml))
        {
            File.WriteAllText(
                indexHtml,
                "<!DOCTYPE html><html><head><title>Stackroot</title></head><body><h1>Stackroot</h1><p>Nginx is running.</p></body></html>",
                Utf8NoBom);
        }
    }

    public static void writeNginxConfig(
        StackrootPaths paths,
        ServicePortSettings portSettings,
        NginxHttpSettings? httpSettings = null)
    {
        var http = NginxHttpSettingsSanitizer.Sanitize(httpSettings);
        if (http.ManageMainConfigManually)
        {
            return;
        }

        var prefix = nginxPrefix(paths);
        var confDir = Path.Combine(prefix, "conf");
        Directory.CreateDirectory(confDir);
        Directory.CreateDirectory(Path.Combine(confDir, "sites-enabled"));
        Directory.CreateDirectory(Path.Combine(prefix, "html"));
        Directory.CreateDirectory(Path.Combine(prefix, "logs"));
        Directory.CreateDirectory(Path.Combine(prefix, "temp"));
        Directory.CreateDirectory(paths.LogsRoot);

        var errorLogPath = Path.Combine(paths.LogsRoot, "nginx-error.log");
        var accessLogPath = Path.Combine(paths.LogsRoot, "nginx-access.log");
        if (!File.Exists(errorLogPath))
        {
            File.WriteAllText(errorLogPath, string.Empty, Utf8NoBom);
        }

        if (!File.Exists(accessLogPath))
        {
            File.WriteAllText(accessLogPath, string.Empty, Utf8NoBom);
        }

        var httpPort = portSettings.Port;
        var httpsPort = portSettings.SslPort ?? 443;
        var host = string.IsNullOrWhiteSpace(portSettings.Host) ? "127.0.0.1" : portSettings.Host;

        var confBuilder = new StringBuilder();
        confBuilder.AppendLine($"worker_processes  {http.WorkerProcesses};");
        confBuilder.AppendLine($"error_log  {NormalizePath(errorLogPath)} {http.ErrorLogLevel};");
        confBuilder.AppendLine("pid        logs/nginx.pid;");
        confBuilder.AppendLine();
        confBuilder.AppendLine("events {");
        confBuilder.AppendLine($"    worker_connections  {http.WorkerConnections};");
        if (http.MultiAccept)
        {
            confBuilder.AppendLine("    multi_accept on;");
        }

        confBuilder.AppendLine("}");
        confBuilder.AppendLine();
        confBuilder.AppendLine("http {");
        confBuilder.AppendLine("    include       mime.types;");
        confBuilder.AppendLine("    default_type  application/octet-stream;");
        if (http.AccessLogEnabled)
        {
            confBuilder.AppendLine($"    access_log    {NormalizePath(accessLogPath)} combined;");
        }
        else
        {
            confBuilder.AppendLine("    access_log    off;");
        }
        if (http.Sendfile)
        {
            confBuilder.AppendLine("    sendfile      on;");
        }

        if (http.TcpNopush)
        {
            confBuilder.AppendLine("    tcp_nopush    on;");
        }

        confBuilder.AppendLine($"    keepalive_timeout  {http.KeepaliveTimeout};");
        confBuilder.AppendLine($"    types_hash_max_size {http.TypesHashMaxSize};");
        confBuilder.AppendLine($"    server_names_hash_bucket_size {http.ServerNamesHashBucketSize};");
        confBuilder.AppendLine($"    client_max_body_size {http.ClientMaxBodySize};");
        AppendGzipDirectives(confBuilder, http);
        NginxStabilityDirectives.AppendHttpDefaults(confBuilder, http);
        confBuilder.AppendLine();
        confBuilder.AppendLine("    server {");
        confBuilder.AppendLine($"        listen       {httpPort};");
        confBuilder.AppendLine($"        listen       [::]:{httpPort};");
        confBuilder.AppendLine($"        server_name  {host} localhost;");
        confBuilder.AppendLine();
        confBuilder.AppendLine("        location / {");
        confBuilder.AppendLine("            root   html;");
        confBuilder.AppendLine("            index  index.html index.htm;");
        confBuilder.AppendLine("        }");
        confBuilder.AppendLine("    }");
        confBuilder.AppendLine();
        confBuilder.AppendLine("    include sites-enabled/*.conf;");
        confBuilder.AppendLine("}");
        var conf = confBuilder.ToString();

        if (portSettings.SslEnabled != false)
        {
            var crtPath = Path.Combine(confDir, "ssl", "dev.crt");
            var keyPath = Path.Combine(confDir, "ssl", "dev.key");
            if (File.Exists(crtPath) && File.Exists(keyPath))
            {
                var sslBlock = $@"
    server {{
        listen       {httpsPort} ssl;
        listen       [::]:{httpsPort} ssl;
        server_name  {host} localhost;

        ssl_certificate      ssl/dev.crt;
        ssl_certificate_key  ssl/dev.key;

        location / {{
            root   html;
            index  index.html index.htm;
        }}
    }}

    include sites-enabled/*.conf;";

                conf = conf.Replace("include sites-enabled/*.conf;", sslBlock, StringComparison.Ordinal);
            }
        }

        File.WriteAllText(Path.Combine(confDir, "nginx.conf"), conf, Utf8NoBom);
    }

    private static void AppendGzipDirectives(StringBuilder confBuilder, NginxHttpSettings http)
    {
        if (!http.GzipEnabled)
        {
            return;
        }

        confBuilder.AppendLine("    gzip  on;");
        confBuilder.AppendLine($"    gzip_comp_level {http.GzipCompLevel};");
        confBuilder.AppendLine($"    gzip_min_length {http.GzipMinLength};");
        confBuilder.AppendLine("    gzip_proxied any;");
        confBuilder.AppendLine("    gzip_vary on;");
        confBuilder.AppendLine("    gzip_types");
        foreach (var type in NginxHttpDefaults.GzipTypes)
        {
            confBuilder.AppendLine($"        {type}");
        }

        confBuilder.AppendLine("    ;");
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }

    private const string DefaultFastCgiParams = @"fastcgi_param  QUERY_STRING       $query_string;
fastcgi_param  REQUEST_METHOD     $request_method;
fastcgi_param  CONTENT_TYPE       $content_type;
fastcgi_param  CONTENT_LENGTH     $content_length;

fastcgi_param  SCRIPT_NAME        $fastcgi_script_name;
fastcgi_param  REQUEST_URI        $request_uri;
fastcgi_param  DOCUMENT_URI       $document_uri;
fastcgi_param  DOCUMENT_ROOT      $document_root;
fastcgi_param  SERVER_PROTOCOL    $server_protocol;
fastcgi_param  REQUEST_SCHEME     $scheme;
fastcgi_param  HTTPS              $https if_not_empty;

fastcgi_param  GATEWAY_INTERFACE  CGI/1.1;
fastcgi_param  SERVER_SOFTWARE    nginx/$nginx_version;
fastcgi_param  REMOTE_ADDR        $remote_addr;
fastcgi_param  REMOTE_PORT        $remote_port;
fastcgi_param  SERVER_ADDR        $server_addr;
fastcgi_param  SERVER_PORT        $server_port;
fastcgi_param  SERVER_NAME        $server_name;
fastcgi_param  REDIRECT_STATUS    200;
";
}
