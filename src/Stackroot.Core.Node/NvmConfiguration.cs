using System.Text;
using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Node;

public static class NvmConfiguration
{
    public static void WriteSettingsFile(string nvmHome, StackrootPaths paths)
    {
        var versionsRoot = NodePaths.VersionsRoot(paths);
        var symlink = NodePaths.SymlinkPath(paths);
        Directory.CreateDirectory(versionsRoot);
        Directory.CreateDirectory(nvmHome);

        var settingsPath = Path.Combine(nvmHome, "settings.txt");
        var settingsContent = string.Join(
            "\r\n",
            $"root: {versionsRoot}",
            $"path: {symlink}",
            "arch: 64",
            "proxy: none",
            "node_mirror: https://nodejs.org/dist/");
        File.WriteAllText(settingsPath, settingsContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static void PrepareSymlinkDirectory(StackrootPaths paths)
    {
        var symlink = NodePaths.SymlinkPath(paths);
        Directory.CreateDirectory(Path.GetDirectoryName(symlink)!);
        if (Directory.Exists(symlink))
        {
            var attributes = File.GetAttributes(symlink);
            if (!attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                Directory.Delete(symlink, recursive: true);
            }
        }
    }
}
