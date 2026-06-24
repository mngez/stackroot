using System.Text;
using Stackroot.Core.Nginx;
using Stackroot.Core.Sites.Models;
using NginxHttpSettings = Stackroot.Core.Abstractions.NginxHttpSettings;

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
        bool sslEnabled = false,
        NginxHttpSettings? httpSettings = null)
    {
        ArgumentNullException.ThrowIfNull(site);
        Directory.CreateDirectory(NginxSitesEnabledDirectory);
        var config = BuildConfig(site, httpPort, phpFastCgiEndpoint ?? _phpFastCgiEndpoint, phpRcDirectory, httpsPort, sslEnabled, httpSettings);
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
        bool sslEnabled = false,
        NginxHttpSettings? httpSettings = null)
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
                sb.AppendLine($"    server_name {SiteDomainNames.FormatNginxServerName(site)};");
                sb.AppendLine($"    return 301 https://$host:{sslPort}$request_uri;");
                sb.AppendLine("}");
                sb.AppendLine();
            }

            AppendServerBlock(sb, site, sslPort, normalizedRoot, fastCgi, phpRc, ssl: true, httpSettings);

            if (site.ForceHttps != true)
            {
                sb.AppendLine();
                AppendServerBlock(sb, site, httpPort, normalizedRoot, fastCgi, phpRc, ssl: false, httpSettings);
            }

            return sb.ToString();
        }

        AppendServerBlock(sb, site, httpPort, normalizedRoot, fastCgi, phpRc, ssl: false, httpSettings);
        return sb.ToString();
    }

    private static void AppendServerBlock(
        StringBuilder sb,
        Site site,
        int listenPort,
        string normalizedRoot,
        string fastCgi,
        string? phpRc,
        bool ssl,
        NginxHttpSettings? httpSettings)
    {
        sb.AppendLine("server {");
        sb.AppendLine(ssl ? $"    listen {listenPort} ssl;" : $"    listen {listenPort};");
        sb.AppendLine($"    server_name {SiteDomainNames.FormatNginxServerName(site)};");
        if (ssl)
        {
            sb.AppendLine($"    ssl_certificate      {DevSslCertificateManager.NginxSslCertificateRel};");
            sb.AppendLine($"    ssl_certificate_key  {DevSslCertificateManager.NginxSslCertificateKeyRel};");
        }

        sb.AppendLine($"    root {normalizedRoot};");
        sb.AppendLine("    index index.php index.html index.htm;");
        sb.AppendLine();
        sb.AppendLine("    location / {");
        sb.AppendLine($"        {BuildMainTryFiles(site)}");
        sb.AppendLine("    }");
        sb.AppendLine();

        foreach (var proxy in site.DevProxies ?? [])
        {
            if (!proxy.Enabled || string.IsNullOrWhiteSpace(SiteDevProxyDirectives.TryGetProxyPass(proxy, httpSettings)))
            {
                continue;
            }

            var (kind, pattern) = SiteDevProxyLocation.Normalize(proxy.LocationKind, proxy.LocationPath);
            if (SiteDevProxyLocation.Validate(kind, pattern) is not null)
            {
                continue;
            }

            var location = SiteDevProxyLocation.Format(kind, pattern);
            sb.AppendLine($"    location {location} {{");
            SiteDevProxyDirectives.AppendLocationBlock(sb, proxy, httpSettings);
            NginxStabilityDirectives.AppendProxyLocation(sb, httpSettings);
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
        NginxStabilityDirectives.AppendFastCgiLocation(sb, httpSettings);
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
