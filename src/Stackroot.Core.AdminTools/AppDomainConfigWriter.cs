using System.Text;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Nginx;
using Stackroot.Core.Services;
using Stackroot.Core.Settings;
using Stackroot.Core.Sites.Persistence;
using Stackroot.Core.Windows;

namespace Stackroot.Core.AdminTools;

public sealed class AppDomainConfigWriter
{
    public const string ConfFileName = "stackroot-app.conf";
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly StackrootPaths _paths;
    private readonly SettingsStore _settingsStore;
    private readonly SiteStore _siteStore;
    private readonly InstallRegistryStore _registryStore;
    private readonly PhpMyAdminManager _phpMyAdminManager;
    private readonly PhpRedisAdminManager _phpRedisAdminManager;
    private readonly HostsFileEditor _hostsFileEditor;

    public AppDomainConfigWriter(
        StackrootPaths paths,
        SettingsStore settingsStore,
        SiteStore siteStore,
        InstallRegistryStore registryStore,
        PhpMyAdminManager phpMyAdminManager,
        PhpRedisAdminManager phpRedisAdminManager,
        HostsFileEditor hostsFileEditor)
    {
        _paths = paths;
        _settingsStore = settingsStore;
        _siteStore = siteStore;
        _registryStore = registryStore;
        _phpMyAdminManager = phpMyAdminManager;
        _phpRedisAdminManager = phpRedisAdminManager;
        _hostsFileEditor = hostsFileEditor;
    }

    public void Write()
    {
        var settings = _settingsStore.Load();
        var appDomain = ResolveAppDomain(settings);
        if (string.IsNullOrWhiteSpace(appDomain))
        {
            Remove();
            return;
        }

        if (settings.Services.TryGetValue(ServiceId.Nginx, out var nginxSettings))
        {
            var nginxPackageId = nginxSettings.PackageId ?? SettingsDefaults.DefaultServices()[ServiceId.Nginx].PackageId;
            var nginxInstalled = string.IsNullOrWhiteSpace(nginxPackageId) ? null : _registryStore.GetById(nginxPackageId);
            if (nginxInstalled is not null)
            {
                NginxRuntime.setupNginxRuntime(_paths, nginxInstalled.InstallPath);
            }
        }

        var nginxPort = ResolveNginxPort(settings);
        var portSuffix = nginxPort == 80 ? string.Empty : $":{nginxPort}";
        var sites = _siteStore.List()
            .Where(site => site.Enabled)
            .Select(site => new AppDomainSiteEntry(site.Name, site.Domain))
            .ToList();

        var pmaStatus = _phpMyAdminManager.GetStatus();
        string? pmaUrl = null;
        string? pmaLabel = null;
        if (pmaStatus.Enabled && pmaStatus.PackageInstalled && !string.IsNullOrWhiteSpace(pmaStatus.Url))
        {
            pmaUrl = pmaStatus.Url;
            pmaLabel = $"{pmaStatus.BaseDomain}/{pmaStatus.Path}";
        }

        var praStatus = _phpRedisAdminManager.GetStatus();
        string? praUrl = null;
        string? praLabel = null;
        if (praStatus.Enabled && praStatus.PackageInstalled && !string.IsNullOrWhiteSpace(praStatus.Url))
        {
            praUrl = praStatus.Url;
            praLabel = praStatus.OpenLabel;
        }

        string? mailpitUrl = null;
        string? mailpitLabel = null;
        var mailpitLocations = string.Empty;
        if (settings.Mailpit.Enabled)
        {
            mailpitLabel = $"{appDomain}/{MailpitManager.WebPath}";
            mailpitUrl = $"http://{appDomain}{portSuffix}/{MailpitManager.WebPath}/";
            mailpitLocations = BuildMailpitPathLocations(settings.Mailpit.WebPort);
        }

        if (settings.Sites.AutoHosts)
        {
            _hostsFileEditor.UpsertHost(appDomain);
        }

        var prefix = NginxRuntime.nginxPrefix(_paths);
        var confDir = Path.Combine(prefix, "conf", "sites-enabled");
        var appHtmlDir = Path.Combine(prefix, "html", "app");
        Directory.CreateDirectory(confDir);
        Directory.CreateDirectory(appHtmlDir);

        TryCopyBrandIcon(appHtmlDir);

        var indexHtml = Path.Combine(appHtmlDir, "index.html");
        File.WriteAllText(
            indexHtml,
            GenerateLandingHtml(appDomain, pmaUrl, pmaLabel, praUrl, praLabel, mailpitUrl, mailpitLabel, sites),
            Utf8NoBom);

        var confPath = Path.Combine(confDir, ConfFileName);
        var root = appHtmlDir.Replace('\\', '/');
        var sb = new StringBuilder();
        sb.AppendLine($"# Stackroot app domain — {appDomain}");
        sb.AppendLine("server {");
        sb.AppendLine($"    listen       {nginxPort};");
        sb.AppendLine($"    listen       [::]:{nginxPort};");
        sb.AppendLine($"    server_name  {appDomain};");
        sb.AppendLine();
        sb.AppendLine($"    root   {root};");
        sb.AppendLine("    index  index.html;");
        if (!string.IsNullOrWhiteSpace(mailpitLocations))
        {
            sb.Append(mailpitLocations);
            sb.AppendLine();
        }

        var pmaLocations = _phpMyAdminManager.GetPathModeNginxLocations();
        if (!string.IsNullOrWhiteSpace(pmaLocations))
        {
            sb.Append(pmaLocations);
        }

        var praLocations = _phpRedisAdminManager.GetPathModeNginxLocations();
        if (!string.IsNullOrWhiteSpace(praLocations))
        {
            sb.Append(praLocations);
        }

        sb.AppendLine("    location = / {");
        sb.AppendLine("        try_files /index.html =404;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    location / {");
        sb.AppendLine("        try_files $uri $uri/ =404;");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        File.WriteAllText(confPath, sb.ToString(), Utf8NoBom);
    }

    public void Remove()
    {
        var confPath = Path.Combine(NginxRuntime.nginxPrefix(_paths), "conf", "sites-enabled", ConfFileName);
        if (File.Exists(confPath))
        {
            File.Delete(confPath);
        }
    }

    internal static string GenerateLandingHtml(
        string appDomain,
        string? pmaUrl,
        string? pmaLabel,
        string? praUrl,
        string? praLabel,
        string? mailpitUrl,
        string? mailpitLabel,
        IReadOnlyList<AppDomainSiteEntry> sites)
    {
        var pmaBlock = pmaUrl is not null && pmaLabel is not null
            ? $"<li><a href=\"{EscapeHtml(pmaUrl)}\">phpMyAdmin</a> — <code>{EscapeHtml(pmaLabel)}</code></li>"
            : string.Empty;
        var praBlock = praUrl is not null && praLabel is not null
            ? $"<li><a href=\"{EscapeHtml(praUrl)}\">phpRedisAdmin</a> — <code>{EscapeHtml(praLabel)}</code></li>"
            : string.Empty;
        var mailpitBlock = mailpitUrl is not null && mailpitLabel is not null
            ? $"<li><a href=\"{EscapeHtml(mailpitUrl)}\">Mailpit</a> — <code>{EscapeHtml(mailpitLabel)}</code></li>"
            : string.Empty;

        var siteItems = sites.Count > 0
            ? string.Join(
                Environment.NewLine,
                sites.Select(site =>
                    $"<li><a href=\"http://{EscapeHtml(site.Domain)}/\">{EscapeHtml(site.Name)}</a> — <code>{EscapeHtml(site.Domain)}</code></li>"))
            : "<li class=\"muted\">No sites yet — create one in Stackroot.</li>";

        var toolsFallback = string.IsNullOrEmpty(pmaBlock) && string.IsNullOrEmpty(praBlock) && string.IsNullOrEmpty(mailpitBlock)
            ? "<li class=\"muted\">No tools configured</li>"
            : string.Empty;

        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1" />
              <title>Stackroot — {{EscapeHtml(appDomain)}}</title>
              <link rel="icon" type="image/png" href="/icon.png" />
              <style>
                :root { color-scheme: light dark; font-family: system-ui, sans-serif; }
                body { max-width: 42rem; margin: 2rem auto; padding: 0 1rem; line-height: 1.5; }
                .brand { display: flex; align-items: center; gap: 0.85rem; margin-bottom: 0.5rem; }
                .brand img { width: 48px; height: 48px; border-radius: 12px; }
                h1 { font-size: 1.6rem; margin: 0; }
                .lead { color: #666; margin-top: 0; }
                ul { padding-left: 1.2rem; }
                code { font-size: 0.92em; }
                .muted { color: #888; }
                a { color: #0a7; }
              </style>
            </head>
            <body>
              <div class="brand">
                <img src="/icon.png" alt="" width="48" height="48" />
                <h1>Stackroot</h1>
              </div>
              <p class="lead">Local development hub — <code>{{EscapeHtml(appDomain)}}</code></p>
              <h2>Tools</h2>
              <ul>
                {{pmaBlock}}{{praBlock}}{{mailpitBlock}}{{toolsFallback}}
              </ul>
              <h2>Sites</h2>
              <ul>
                {{siteItems}}
              </ul>
            </body>
            </html>
            """;
    }

    private static string BuildMailpitPathLocations(int webPort) =>
        $@"
    location = /{MailpitManager.WebPath} {{
        return 301 /{MailpitManager.WebPath}/;
    }}

    location /{MailpitManager.WebPath}/ {{
        proxy_pass http://127.0.0.1:{webPort}/{MailpitManager.WebPath}/;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection ""upgrade"";
    }}
";

    private void TryCopyBrandIcon(string appHtmlDir)
    {
        var candidates = new[]
        {
            Path.Combine(_paths.ResourcesRoot, "icons", "icon.png"),
            Path.Combine(_paths.ResourcesRoot, "icon.png"),
            Path.Combine(AppContext.BaseDirectory, "assets", "icons", "icon.png"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "assets", "icons", "icon.png"))
        };

        var destination = Path.Combine(appHtmlDir, "icon.png");
        foreach (var source in candidates)
        {
            if (!File.Exists(source))
            {
                continue;
            }

            File.Copy(source, destination, overwrite: true);
            return;
        }
    }

    private static string ResolveAppDomain(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.General.AppDomain) ? "stackroot.test" : settings.General.AppDomain.Trim();

    private static int ResolveNginxPort(AppSettings settings)
    {
        if (settings.Services.TryGetValue(ServiceId.Nginx, out var nginx) && nginx.Port > 0)
        {
            return nginx.Port;
        }

        return SettingsDefaults.DefaultServices()[ServiceId.Nginx].Port;
    }

    private static string EscapeHtml(string value) =>
        value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
}

public sealed record AppDomainSiteEntry(string Name, string Domain);
