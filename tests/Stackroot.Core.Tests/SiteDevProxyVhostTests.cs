using System.Text.RegularExpressions;
using Stackroot.Core.Sites.Models;
using Stackroot.Core.Sites.Nginx;
using Xunit;

namespace Stackroot.Core.Tests;

public sealed class SiteDevProxyVhostTests
{
    [Fact]
    public void Root_websocket_proxy_replaces_the_default_location()
    {
        var config = Build(new SiteDevProxy
        {
            Id = "socket",
            Name = "socket",
            Enabled = true,
            LocationKind = SiteDevProxyLocationKind.Prefix,
            LocationPath = "/",
            TargetUrl = "http://127.0.0.1:3000",
            Websocket = true
        });

        Assert.Single(Regex.Matches(config, @"location / \{"));
        Assert.DoesNotContain("try_files $uri $uri/ /index.html;", config);
        Assert.Contains("proxy_pass http://127.0.0.1:3000;", config);
        Assert.Contains("proxy_http_version 1.1;", config);
        Assert.Contains("proxy_set_header Upgrade $http_upgrade;", config);
        Assert.Contains("proxy_set_header Connection \"upgrade\";", config);
        Assert.Contains("proxy_buffering off;", config);
        Assert.DoesNotContain("proxy_buffering on;", config);
    }

    [Fact]
    public void Subpath_proxy_keeps_the_site_location()
    {
        var config = Build(new SiteDevProxy
        {
            Id = "vite",
            Name = "vite",
            Enabled = true,
            LocationKind = SiteDevProxyLocationKind.Prefix,
            LocationPath = "/vite/",
            TargetUrl = "http://127.0.0.1:3000",
            Websocket = true
        });

        Assert.Single(Regex.Matches(config, @"location / \{"));
        Assert.Contains("location /vite/ {", config);
        Assert.Contains("try_files $uri $uri/ /index.html;", config);
    }

    [Fact]
    public void Empty_site_can_store_no_php_selection()
    {
        Assert.False(SiteTemplates.Resolve(SiteTemplateIds.Static).RequiresPhp);
        Assert.True(SiteTemplates.Resolve(SiteTemplateIds.Laravel).RequiresPhp);
        Assert.Null(SitePhpSelection.ToStoredId("no-php"));
        Assert.Null(SitePhpSelection.ToStoredId("  "));
        Assert.Equal("php-8.3.36", SitePhpSelection.ToStoredId(" php-8.3.36 "));
    }

    private static string Build(SiteDevProxy proxy)
    {
        var site = new Site
        {
            Id = "edu-test",
            Name = "edu",
            Domain = "edu.test",
            Template = SiteTemplateIds.Static,
            Path = @"C:\sites\edu",
            DevProxies = [proxy]
        };

        return new SiteNginxVhostWriter(Path.GetTempPath()).BuildConfig(site);
    }
}
