using Stackroot.Core.Sites.Models;

namespace Stackroot.Core.Sites;

public static class SiteProvisioner
{
    public static void ScaffoldDirectory(string siteRoot, string documentRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteRoot);

        var rel = NormalizeDocumentRoot(documentRoot);
        var docDir = rel == "." ? siteRoot : Path.Combine(siteRoot, rel);
        Directory.CreateDirectory(docDir);

        if (rel == "public")
        {
            Directory.CreateDirectory(siteRoot);
        }
    }

    public static void ScaffoldFiles(Site site)
    {
        ArgumentNullException.ThrowIfNull(site);

        var dir = PublicDir(site);
        Directory.CreateDirectory(dir);

        if (string.Equals(site.Template, SiteTemplateIds.Laravel, StringComparison.OrdinalIgnoreCase))
        {
            WriteIfMissing(
                Path.Combine(site.Path, "README.txt"),
                $"Laravel site — {site.Domain}{Environment.NewLine}Install Laravel here, web root is public/{Environment.NewLine}");
        }

        if (string.Equals(site.Template, SiteTemplateIds.Wordpress, StringComparison.OrdinalIgnoreCase))
        {
            WriteIfMissing(
                Path.Combine(site.Path, "README.txt"),
                $"WordPress site — {site.Domain}{Environment.NewLine}Install WordPress in this folder.{Environment.NewLine}");
        }

        if (!string.IsNullOrWhiteSpace(site.PhpVersionId))
        {
            var indexPhp = Path.Combine(dir, "index.php");
            if (!File.Exists(indexPhp))
            {
                File.WriteAllText(indexPhp,
                    $"""
                    <?php
                    // {site.Domain} — {site.Template}
                    echo '<h1>{EscapePhpString(site.Name)}</h1>';
                    echo '<p>Template: {site.Template}</p>';
                    echo '<p>PHP ' . PHP_VERSION . '</p>';

                    """);
            }

            return;
        }

        var indexHtml = Path.Combine(dir, "index.html");
        if (!File.Exists(indexHtml))
        {
            File.WriteAllText(indexHtml,
                $"""
                <!DOCTYPE html>
                <html lang="en">
                <head><meta charset="UTF-8" /><title>{EscapeHtml(site.Name)}</title></head>
                <body><h1>{EscapeHtml(site.Name)}</h1><p>Static site — Stackroot</p></body>
                </html>
                """);
        }
    }

    private static string PublicDir(Site site)
    {
        var rel = NormalizeDocumentRoot(site.DocumentRoot);
        return rel == "." ? site.Path : Path.Combine(site.Path, rel);
    }

    private static string NormalizeDocumentRoot(string? documentRoot)
    {
        var normalized = string.IsNullOrWhiteSpace(documentRoot) ? "." : documentRoot.Trim();
        return normalized.TrimStart('/', '\\');
    }

    private static void WriteIfMissing(string filePath, string content)
    {
        if (!File.Exists(filePath))
        {
            File.WriteAllText(filePath, content);
        }
    }

    private static string EscapeHtml(string value) =>
        value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;");

    private static string EscapePhpString(string value) =>
        value.Replace("\\", "\\\\").Replace("'", "\\'");
}
