using System.Diagnostics;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Nginx;

namespace Stackroot.Core.AdminTools;

public static class AdminToolPathLinker
{
    public static string LinkPathTool(StackrootPaths paths, string webRoot, string pathSegment)
    {
        var appRoot = Path.Combine(NginxRuntime.nginxPrefix(paths), "html", "app");
        Directory.CreateDirectory(appRoot);

        var segment = string.IsNullOrWhiteSpace(pathSegment) ? "tool" : pathSegment.Trim('/').Trim();
        var link = Path.Combine(appRoot, segment);
        RemovePathLink(link);

        if (TryCreateSymbolicLink(link, webRoot)
            || TryCreateJunction(link, webRoot)
            || TryMirrorDirectory(webRoot, link))
        {
            return appRoot.Replace('\\', '/');
        }

        throw new InvalidOperationException(
            $"Could not link {segment} into the app domain. Run Stackroot as administrator or enable Windows Developer Mode for symlinks.");
    }

    public static void RemovePathTool(StackrootPaths paths, string pathSegment)
    {
        var segment = string.IsNullOrWhiteSpace(pathSegment) ? "tool" : pathSegment.Trim('/').Trim();
        var link = Path.Combine(NginxRuntime.nginxPrefix(paths), "html", "app", segment);
        RemovePathLink(link);
    }

    private static bool TryCreateSymbolicLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return Directory.Exists(link) || File.Exists(link);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            process.WaitForExit();
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryMirrorDirectory(string source, string destination)
    {
        try
        {
            CopyDirectory(source, destination);
            return Directory.Exists(destination);
        }
        catch
        {
            return false;
        }
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        var sourceRoot = Path.GetFullPath(sourceDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var destinationRoot = Path.GetFullPath(destinationDir);

        Directory.CreateDirectory(destinationRoot);
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, file);
            var target = Path.Combine(destinationRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void RemovePathLink(string link)
    {
        if (!Directory.Exists(link) && !File.Exists(link))
        {
            return;
        }

        try
        {
            var info = new DirectoryInfo(link);
            if (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                info.Delete();
                return;
            }
        }
        catch
        {
            // Fall through to recursive delete.
        }

        try
        {
            Directory.Delete(link, recursive: true);
        }
        catch
        {
            // Best effort.
        }
    }
}
