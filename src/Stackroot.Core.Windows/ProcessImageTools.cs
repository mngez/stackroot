using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Stackroot.Core.Windows;

public static class ProcessImageTools
{
    public static bool TryGetExecutablePath(int pid, out string? executablePath)
    {
        executablePath = null;
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            executablePath = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                executablePath = Path.GetFullPath(executablePath);
                return true;
            }
        }
        catch
        {
            // MainModule often throws Access Denied for services — fall through.
        }

        return TryQueryFullProcessImageName(pid, out executablePath);
    }

    public static bool IsExecutableUnderInstallPath(int pid, string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !TryGetExecutablePath(pid, out var executablePath))
        {
            return false;
        }

        return ExecutablePathIsUnderInstallPath(executablePath!, installPath);
    }

    public static bool ExecutablePathIsUnderInstallPath(string executablePath, string installPath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || string.IsNullOrWhiteSpace(installPath))
        {
            return false;
        }

        var normalizedExecutable = Path.GetFullPath(executablePath);
        var normalizedInstall = Path.GetFullPath(
            installPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return normalizedExecutable.StartsWith(normalizedInstall + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedExecutable, normalizedInstall, StringComparison.OrdinalIgnoreCase);
    }

    public static bool ExecutableReferencesInstallFolder(int pid, string installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !TryGetExecutablePath(pid, out var executablePath))
        {
            return false;
        }

        return ExecutablePathReferencesInstallFolder(executablePath!, installPath);
    }

    public static bool ExecutablePathReferencesInstallFolder(string executablePath, string installPath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || string.IsNullOrWhiteSpace(installPath))
        {
            return false;
        }

        var normalizedInstall = Path.GetFullPath(
            installPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var installFolder = Path.GetFileName(normalizedInstall);
        if (string.IsNullOrWhiteSpace(installFolder))
        {
            return false;
        }

        // Match full path segment only — avoid "mysql-8.4.4" matching "mysql-8.4.4-winx64".
        var segment = Path.DirectorySeparatorChar + installFolder + Path.DirectorySeparatorChar;
        var altSegment = Path.AltDirectorySeparatorChar + installFolder + Path.AltDirectorySeparatorChar;
        return executablePath.Contains(segment, StringComparison.OrdinalIgnoreCase)
            || executablePath.Contains(altSegment, StringComparison.OrdinalIgnoreCase)
            || executablePath.EndsWith(
                Path.DirectorySeparatorChar + installFolder,
                StringComparison.OrdinalIgnoreCase)
            || executablePath.EndsWith(
                Path.AltDirectorySeparatorChar + installFolder,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryQueryFullProcessImageName(int pid, out string? executablePath)
    {
        executablePath = null;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var capacity = 1024;
            var builder = new StringBuilder(capacity);
            if (!QueryFullProcessImageName(handle, 0, builder, ref capacity))
            {
                return false;
            }

            executablePath = Path.GetFullPath(builder.ToString());
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(
        IntPtr hProcess,
        int dwFlags,
        StringBuilder lpExeName,
        ref int lpdwSize);
}
