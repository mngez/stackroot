using Stackroot.Core.Sites.Models;
using Stackroot.Core.Sites.Nginx;
using Xunit;

namespace Stackroot.Core.Tests.Sites;

public sealed class SiteDevProxyLocationTests
{
    [Fact]
    public void Format_regex_writes_valid_nginx_location()
    {
        const string pattern = "^/lobby/v1/[0-9a-f]{32}/lobbies/connect$";

        var formatted = SiteDevProxyLocation.Format(SiteDevProxyLocationKind.Regex, pattern);

        Assert.Equal($"~ \"{pattern}\"", formatted);
    }

    [Fact]
    public void Format_notifications_regex_writes_valid_nginx_location()
    {
        const string pattern = "^/(notifications/v1/[0-9a-f]{32}/connect)$";

        var formatted = SiteDevProxyLocation.Format(SiteDevProxyLocationKind.Regex, pattern);

        Assert.Equal($"~ \"{pattern}\"", formatted);
    }

    [Fact]
    public void Format_regex_without_braces_stays_unquoted()
    {
        const string pattern = "^/api/.+$";

        var formatted = SiteDevProxyLocation.Format(SiteDevProxyLocationKind.Regex, pattern);

        Assert.Equal($"~ {pattern}", formatted);
    }

    [Fact]
    public void ParsePatternInput_strips_full_nginx_location_line()
    {
        const string pasted = "location ~ ^/(notifications/v1/[0-9a-f]{32}/connect)$ {";

        var pattern = SiteDevProxyLocation.ParsePatternInput(SiteDevProxyLocationKind.Regex, pasted);

        Assert.Equal("^/(notifications/v1/[0-9a-f]{32}/connect)$", pattern);
    }

    [Fact]
    public void ParsePatternInput_uses_first_line_from_multiline_paste()
    {
        const string pasted = """
            location ~ ^/(notifications/v1/[0-9a-f]{32}/connect)$ {
                proxy_pass http://127.0.0.1:8080;
            """;

        var pattern = SiteDevProxyLocation.ParsePatternInput(SiteDevProxyLocationKind.Regex, pasted);

        Assert.Equal("^/(notifications/v1/[0-9a-f]{32}/connect)$", pattern);
    }

    [Fact]
    public void Normalize_splits_legacy_tilde_prefix()
    {
        var (kind, pattern) = SiteDevProxyLocation.Normalize(
            null,
            "~ ^/lobby/v1/[0-9a-f]{32}/lobbies/connect$");

        Assert.Equal(SiteDevProxyLocationKind.Regex, kind);
        Assert.Equal("^/lobby/v1/[0-9a-f]{32}/lobbies/connect$", pattern);
    }

    [Fact]
    public void Normalize_detects_regex_without_modifier_when_kind_missing()
    {
        var (kind, pattern) = SiteDevProxyLocation.Normalize(
            null,
            "^/notifications/v1/[0-9a-f]{32}/connect$");

        Assert.Equal(SiteDevProxyLocationKind.Regex, kind);
        Assert.Equal("^/notifications/v1/[0-9a-f]{32}/connect$", pattern);
    }

    [Fact]
    public void Validate_rejects_regex_pasted_as_prefix()
    {
        var error = SiteDevProxyLocation.Validate(
            SiteDevProxyLocationKind.Prefix,
            "^/lobby/v1/[0-9a-f]{32}/lobbies/connect$");

        Assert.NotNull(error);
    }

    [Fact]
    public void Validate_accepts_notifications_regex_pattern()
    {
        var error = SiteDevProxyLocation.Validate(
            SiteDevProxyLocationKind.Regex,
            "^/(notifications/v1/[0-9a-f]{32}/connect)$");

        Assert.Null(error);
    }
}
