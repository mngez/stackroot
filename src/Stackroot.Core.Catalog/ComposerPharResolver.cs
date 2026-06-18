using System.Text;

namespace Stackroot.Core.Catalog;

public static class ComposerPharResolver
{
    public static string? FindPharPath(string installPath)
    {
        if (!Directory.Exists(installPath))
        {
            return null;
        }

        var direct = Path.Combine(installPath, "composer.phar");
        if (File.Exists(direct))
        {
            return direct;
        }

        return Directory.EnumerateFiles(installPath, "composer*.phar", SearchOption.TopDirectoryOnly)
            .OrderByDescending(static path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    public static string? EnsureStandardPharPath(string installPath)
    {
        var found = FindPharPath(installPath);
        if (found is null)
        {
            return null;
        }

        var target = Path.Combine(installPath, "composer.phar");
        if (!string.Equals(found, target, StringComparison.OrdinalIgnoreCase))
        {
            File.Move(found, target, overwrite: true);
        }

        return target;
    }

    public static void WriteComposerBat(string batPath, string phpExePath, string pharPath)
    {
        var php = phpExePath.Replace('/', '\\');
        var phar = pharPath.Replace('/', '\\');
        File.WriteAllText(
            batPath,
            $"@echo off\r\n\"{php}\" \"{phar}\" %*\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
