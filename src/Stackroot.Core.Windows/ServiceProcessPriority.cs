using System.Diagnostics;

namespace Stackroot.Core.Windows;

public static class ServiceProcessPriority
{
    public static void Apply(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.PriorityClass = ProcessPriorityClass.AboveNormal;
            }
        }
        catch
        {
            // Best effort — some hosts restrict priority changes.
        }
    }

    public static void Apply(int processId)
    {
        if (processId <= 0)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            Apply(process);
        }
        catch
        {
            // Process may have already exited.
        }
    }
}
