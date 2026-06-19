using System.Text;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;

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

    public static void writeNginxConfig(StackrootPaths paths, ServicePortSettings settings)
    {
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

        var httpPort = settings.Port;
        var httpsPort = settings.SslPort ?? 443;
        var host = string.IsNullOrWhiteSpace(settings.Host) ? "127.0.0.1" : settings.Host;

        var confBuilder = new StringBuilder();
        confBuilder.AppendLine("worker_processes  1;");
        confBuilder.AppendLine($"error_log  {NormalizePath(errorLogPath)} warn;");
        confBuilder.AppendLine("pid        logs/nginx.pid;");
        confBuilder.AppendLine();
        confBuilder.AppendLine("events {");
        confBuilder.AppendLine("    worker_connections  1024;");
        confBuilder.AppendLine("}");
        confBuilder.AppendLine();
        confBuilder.AppendLine("http {");
        confBuilder.AppendLine("    include       mime.types;");
        confBuilder.AppendLine("    default_type  application/octet-stream;");
        confBuilder.AppendLine($"    access_log    {NormalizePath(accessLogPath)} combined;");
        confBuilder.AppendLine("    sendfile      on;");
        confBuilder.AppendLine("    keepalive_timeout  65;");
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

        if (settings.SslEnabled != false)
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
