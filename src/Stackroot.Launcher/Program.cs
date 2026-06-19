using System.Diagnostics;

namespace Stackroot.Launcher;

internal static class Program
{
    private const string CurrentVersionFileName = "current.txt";
    private const string AppFolderName = "app";
    private const string AppExecutableName = "Stackroot.exe";

    [STAThread]
    private static int Main()
    {
        // Reads current.txt and starts app\{version}\Stackroot.exe. Setup must keep the
        // install root free of legacy 0.2 self-contained runtime files (hostfxr.dll, etc.).
        var installRoot = ResolveInstallRoot();

        var currentFile = Path.Combine(installRoot, CurrentVersionFileName);
        if (!File.Exists(currentFile))
        {
            NativeMessageBox.ShowError(
                $"Missing {CurrentVersionFileName} in the install folder.\n\nReinstall Stackroot from the latest installer.");
            return 1;
        }

        var version = File.ReadAllText(currentFile).Trim();
        if (string.IsNullOrWhiteSpace(version))
        {
            NativeMessageBox.ShowError(
                $"{CurrentVersionFileName} is empty.\n\nReinstall Stackroot from the latest installer.");
            return 1;
        }

        var appDir = Path.Combine(installRoot, AppFolderName, version);
        var appExe = Path.Combine(appDir, AppExecutableName);
        if (!File.Exists(appExe))
        {
            NativeMessageBox.ShowError(
                $"App not found for version {version}:\n{appExe}\n\nReinstall or run the Stackroot updater.");
            return 1;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = appExe,
                WorkingDirectory = appDir,
                UseShellExecute = false,
                Arguments = BuildUserArgumentString()
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                NativeMessageBox.ShowError("Stackroot could not start the application process.");
                return 1;
            }

            return 0;
        }
        catch (Exception ex)
        {
            NativeMessageBox.ShowError($"Failed to start Stackroot:\n{ex.Message}");
            return 1;
        }
    }

    private static string ResolveInstallRoot()
    {
        var exePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            return Path.GetDirectoryName(exePath)!;
        }

        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string BuildUserArgumentString() =>
        string.Join(' ', Environment.GetCommandLineArgs().Skip(1).Select(Quote));

    private static string Quote(string value) =>
        value.Contains(' ') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\\\"")}\""
            : value;
}
