using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Nginx;

public static class NginxHttpDefaults
{
    public static NginxHttpSettings Create() => new();

    public static readonly IReadOnlyList<string> GzipTypes =
    [
        "application/atom+xml",
        "application/javascript",
        "application/json",
        "application/rss+xml",
        "application/vnd.ms-fontobject",
        "application/x-font-ttf",
        "application/x-web-app-manifest+json",
        "application/xhtml+xml",
        "application/xml",
        "font/opentype",
        "image/svg+xml",
        "image/x-icon",
        "text/css",
        "text/plain",
        "text/x-component"
    ];
}
