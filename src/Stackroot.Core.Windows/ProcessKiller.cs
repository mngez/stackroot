using System.Diagnostics;

namespace Stackroot.Core.Windows;

public static class ProcessKiller
{
    public static bool TryKill(int pid, bool entireTree = true)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            // Don't check HasExited first — it can hang on zombie processes.
            // Kill throws InvalidOperationException if already exited (caught below).
            process.Kill(entireTree);
            process.WaitForExit(5000);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
