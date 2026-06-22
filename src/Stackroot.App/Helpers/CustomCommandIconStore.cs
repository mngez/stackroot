using System.IO;
using Stackroot.Core.IO;

namespace Stackroot.App.Helpers;

internal static class CustomCommandIconStore
{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp"
    };

    public static string IconsDirectory(string siteDataDir) =>
        StackrootPathResolver.SiteCustomCommandIconsPath(siteDataDir);

    public static string? ResolvePath(string siteDataDir, string? iconFileName)
    {
        if (string.IsNullOrWhiteSpace(iconFileName))
        {
            return null;
        }

        var path = Path.Combine(IconsDirectory(siteDataDir), iconFileName);
        return File.Exists(path) ? path : null;
    }

    public static string ImportIcon(string siteDataDir, string commandId, string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(extension) || !AllowedExtensions.Contains(extension))
        {
            extension = ".png";
        }

        Directory.CreateDirectory(IconsDirectory(siteDataDir));
        var fileName = $"{commandId}{extension}";
        File.Copy(sourcePath, Path.Combine(IconsDirectory(siteDataDir), fileName), overwrite: true);
        return fileName;
    }

    public static void DeleteIcon(string siteDataDir, string? iconFileName)
    {
        if (string.IsNullOrWhiteSpace(iconFileName))
        {
            return;
        }

        var path = Path.Combine(IconsDirectory(siteDataDir), iconFileName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
