using Stackroot.Core.Sites.Models;
using Stackroot.Core.Sites.Nginx;
using Xunit;

namespace Stackroot.Core.Tests.Sites;

public sealed class SiteDevProxyDirectivesTests
{
    [Fact]
    public void BuildDefaults_is_minimal_without_websocket()
    {
        var proxy = new SiteDevProxy { TargetUrl = "http://127.0.0.1:9502" };

        var defaults = SiteDevProxyDirectives.BuildDefaults(proxy);

        Assert.Single(defaults);
        Assert.Equal("proxy_pass", defaults[0].Key);
        Assert.Equal("http://127.0.0.1:9502", defaults[0].Value);
    }

    [Fact]
    public void BuildDefaults_adds_websocket_headers_when_enabled()
    {
        var proxy = new SiteDevProxy
        {
            TargetUrl = "http://127.0.0.1:9502",
            Websocket = true
        };

        var defaults = SiteDevProxyDirectives.BuildDefaults(proxy);

        Assert.Equal(3, defaults.Count);
        Assert.Contains(defaults, entry => entry.Key == "proxy_set_header:Upgrade");
        Assert.Contains(defaults, entry => entry.Key == "proxy_set_header:Connection");
    }

    [Fact]
    public void BuildMerged_applies_overrides_on_defaults()
    {
        var proxy = new SiteDevProxy
        {
            TargetUrl = "http://127.0.0.1:9502",
            Websocket = true,
            DirectiveOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["proxy_set_header:Origin"] = "https://api.epicgames.dev",
                ["proxy_set_header:X-Forwarded-Proto"] = "https"
            }
        };

        var merged = SiteDevProxyDirectives.BuildMerged(proxy);
        Assert.Equal("http://127.0.0.1:9502", merged.First(entry => entry.Key == "proxy_pass").Value);
        Assert.Equal("https://api.epicgames.dev", merged.First(entry => entry.Key == "proxy_set_header:Origin").Value);
        Assert.Equal("https", merged.First(entry => entry.Key == "proxy_set_header:X-Forwarded-Proto").Value);
        Assert.Contains(merged, entry => entry.Key == "proxy_set_header:Upgrade");
        Assert.DoesNotContain(merged, entry => entry.Key == "proxy_set_header:Host");
    }

    [Fact]
    public void FormatDirectiveLine_writes_proxy_set_header()
    {
        var line = SiteDevProxyDirectives.FormatDirectiveLine("proxy_set_header:Host", "$host");
        Assert.Equal("proxy_set_header Host $host", line);
    }

    [Fact]
    public void ComputeOverrides_stores_only_differences()
    {
        var proxy = new SiteDevProxy
        {
            TargetUrl = "http://127.0.0.1:9502",
            Websocket = false
        };

        var edited = new List<KeyValuePair<string, string>>
        {
            new("proxy_pass", "http://127.0.0.1:9502"),
            new("proxy_set_header:X-Forwarded-Proto", "https")
        };

        var overrides = SiteDevProxyDirectives.ComputeOverrides(proxy, edited);
        Assert.NotNull(overrides);
        Assert.Equal("https", overrides!["proxy_set_header:X-Forwarded-Proto"]);
        Assert.False(overrides.ContainsKey("proxy_pass"));
    }

    [Fact]
    public void TryGetProxyPass_reads_merged_value()
    {
        var proxy = new SiteDevProxy
        {
            TargetUrl = "http://127.0.0.1:5173",
            DirectiveOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["proxy_pass"] = "http://127.0.0.1:9502"
            }
        };

        Assert.Equal("http://127.0.0.1:9502", SiteDevProxyDirectives.TryGetProxyPass(proxy));
    }
}
