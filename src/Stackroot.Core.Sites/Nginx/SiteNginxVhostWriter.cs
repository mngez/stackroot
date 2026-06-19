using System.Text;
using Stackroot.Core.Sites.Models;

namespace Stackroot.Core.Sites.Nginx;

public sealed class SiteNginxVhostWriter
{
    private readonly string _phpFastCgiEndpoint;

    public SiteNginxVhostWriter(string nginxSitesEnabledDirectory)
        : this(nginxSitesEnabledDirectory, "127.0.0.1:9000")
    {
    }

    public SiteNginxVhostWriter(string nginxSitesEnabledDirectory, string phpFastCgiEndpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nginxSitesEnabledDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(phpFastCgiEndpoint);
        NginxSitesEnabledDirectory = nginxSitesEnabledDirectory;
        _phpFastCgiEndpoint = phpFastCgiEndpoint.Trim();
    }

    public string NginxSitesEnabledDirectory { get; }

    public string GetConfigPath(Site site)
    {
        ArgumentNullException.ThrowIfNull(site);
        return Path.Combine(NginxSitesEnabledDirectory, $"{site.Id}.conf");
    }

    public void Write(
        Site site,
        int httpPort = 80,
        string? phpFastCgiEndpoint = null,
        string? phpRcDirectory = null,
        int? httpsPort = null,
        bool sslEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(site);
        Directory.CreateDirectory(NginxSitesEnabledDirectory);
        var config = BuildConfig(site, httpPort, phpFastCgiEndpoint ?? _phpFastCgiEndpoint, phpRcDirectory, httpsPort, sslEnabled);
        File.WriteAllText(GetConfigPath(site), config);
    }

    public void Remove(Site site)
    {
        var file = GetConfigPath(site);
        if (File.Exists(file))
        {
            File.Delete(file);
        }
    }

    public string BuildConfig(
        Site site,
        int httpPort = 80,
        string? phpFastCgiEndpoint = null,
        string? phpRcDirectory = null,
        int? httpsPort = null,
        bool sslEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(site);
        var fastCgi = string.IsNullOrWhiteSpace(phpFastCgiEndpoint) ? _phpFastCgiEndpoint : phpFastCgiEndpoint.Trim();
        var normalizedRoot = ResolveDocumentRoot(site).Replace('\\', '/');
        var phpRc = string.IsNullOrWhiteSpace(phpRcDirectory) ? null : phpRcDirectory.Trim().Replace('\\', '/');
        var sb = new StringBuilder();
        sb.AppendLine($"# stackroot site: {site.Name} ({site.Id})");

        if (sslEnabled && httpsPort is int sslPort and > 0)
        {
            if (site.ForceHttps == true)
            {
                sb.AppendLine("server {");
                sb.AppendLine($"    listen {httpPort};");
                sb.AppendLine($"    server_name {site.Domain};");
                sb.AppendLine($"    return 301 https://$host:{sslPort}$request_uri;");
                sb.AppendLine("}");
                sb.AppendLine();
            }

            AppendServerBlock(sb, site, sslPort, normalizedRoot, fastCgi, phpRc, ssl: true);

            if (site.ForceHttps != true)
            {
                sb.AppendLine();
                AppendServerBlock(sb, site, httpPort, normalizedRoot, fastCgi, phpRc, ssl: false);
            }

            return sb.ToString();
        }

        AppendServerBlock(sb, site, httpPort, normalizedRoot, fastCgi, phpRc, ssl: false);
        return sb.ToString();
    }

    private static void AppendServerBlock(
        StringBuilder sb,
        Site site,
        int listenPort,
        string normalizedRoot,
        string fastCgi,
        string? phpRc,
        bool ssl)
    {
        sb.AppendLine("server {");
        sb.AppendLine(ssl ? $"    listen {listenPort} ssl;" : $"    listen {listenPort};");
        sb.AppendLine($"    server_name {site.Domain};");
        if (ssl)
        {
            sb.AppendLine("    ssl_certificate      ssl/dev.crt;");
            sb.AppendLine("    ssl_certificate_key  ssl/dev.key;");
        }

        sb.AppendLine($"    root {normalizedRoot};");
        sb.AppendLine("    index index.php index.html index.htm;");
        sb.AppendLine("    client_max_body_size 64m;");
        sb.AppendLine();
        sb.AppendLine("    location / {");
        sb.AppendLine($"        {BuildMainTryFiles(site)}");
        sb.AppendLine("    }");
        sb.AppendLine();

        foreach (var proxy in site.DevProxies ?? [])
        {
            if (!proxy.Enabled || string.IsNullOrWhiteSpace(proxy.TargetUrl))
            {
                continue;
            }

            var location = string.IsNullOrWhiteSpace(proxy.LocationPath) ? "/" : proxy.LocationPath.Trim();
            sb.AppendLine($"    location {location} {{");
            sb.AppendLine($"        proxy_pass {proxy.TargetUrl.Trim()};");
            sb.AppendLine("        proxy_http_version 1.1;");
            sb.AppendLine("        proxy_set_header Host $host;");
            sb.AppendLine("        proxy_set_header X-Real-IP $remote_addr;");
            sb.AppendLine("        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;");
            sb.AppendLine("        proxy_set_header X-Forwarded-Proto $scheme;");
            if (proxy.Websocket == true)
            {
                sb.AppendLine("        proxy_set_header Upgrade $http_upgrade;");
                sb.AppendLine("        proxy_set_header Connection \"upgrade\";");
            }

            sb.AppendLine("    }");
            sb.AppendLine();
        }

        sb.AppendLine("    location ~ \\.php$ {");
        sb.AppendLine("        try_files $uri =404;");
        sb.AppendLine("        include fastcgi_params;");
        sb.AppendLine("        fastcgi_param SCRIPT_FILENAME $document_root$fastcgi_script_name;");
        sb.AppendLine("        fastcgi_param DOCUMENT_ROOT $document_root;");
        if (!string.IsNullOrWhiteSpace(phpRc))
        {
            sb.AppendLine($"        fastcgi_param  PHPRC \"{phpRc}\";");
        }

        sb.AppendLine($"        fastcgi_pass {fastCgi};");
        sb.AppendLine("    }");
        sb.AppendLine("}");
    }

    private static string BuildMainTryFiles(Site site)
    {
        return site.Template.ToLowerInvariant() switch
        {
            SiteTemplateIds.Laravel => "try_files $uri $uri/ /index.php?$query_string;",
            SiteTemplateIds.Wordpress => "try_files $uri $uri/ /index.php?$args;",
            _ => "try_files $uri $uri/ /index.html;"
        };
    }

    private static string ResolveDocumentRoot(Site site)
    {
        var documentRoot = string.IsNullOrWhiteSpace(site.DocumentRoot) ? "." : site.DocumentRoot.Trim();
        if (documentRoot == ".")
        {
            return site.Path;
        }

        return Path.Combine(site.Path, documentRoot);
    }
}
