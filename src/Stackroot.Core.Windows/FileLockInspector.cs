using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace Stackroot.Core.Windows;

public static class FileLockInspector
{
    private const int ErrorMoreData = 234;
    private const int CchRmMaxAppName = 255;
    private const int CchRmMaxSvcName = 63;
    private const int CchRmSessionKey = 32;

    public sealed record LockingProcess(int ProcessId, string ProcessName);

    public static IReadOnlyList<LockingProcess> FindProcessesLockingFile(string filePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(filePath))
        {
            return [];
        }

        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            return [];
        }

        try
        {
            return FindViaRestartManager(fullPath);
        }
        catch
        {
            return [];
        }
    }

    public static IOException CreateAccessException(
        string filePath,
        string actionDescription,
        IOException? innerException = null)
    {
        var processes = FindProcessesLockingFile(filePath);
        var message = FormatLockMessage(filePath, processes, actionDescription);
        return innerException is null
            ? new IOException(message)
            : new IOException(message, innerException);
    }

    public static string FormatLockMessage(
        string filePath,
        IReadOnlyList<LockingProcess> processes,
        string actionDescription)
    {
        var normalizedPath = Path.GetFullPath(filePath);
        if (processes.Count == 0)
        {
            return
                $"Cannot {actionDescription}:{Environment.NewLine}{normalizedPath}{Environment.NewLine}" +
                "The file is in use by another process, but Stackroot could not identify which one. " +
                "Close editors, terminals, or other Stackroot instances that may have this file open.";
        }

        var processLines = processes
            .GroupBy(process => process.ProcessId)
            .Select(group => group.First())
            .OrderBy(process => process.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(process => $"- {process.ProcessName} (PID {process.ProcessId})");

        return
            $"Cannot {actionDescription}:{Environment.NewLine}{normalizedPath}{Environment.NewLine}" +
            "Locking process(es):" + Environment.NewLine +
            string.Join(Environment.NewLine, processLines);
    }

    private static IReadOnlyList<LockingProcess> FindViaRestartManager(string fullPath)
    {
        var sessionKey = Guid.NewGuid().ToString("N")[..CchRmSessionKey];
        var sessionKeyBuilder = new StringBuilder(sessionKey);

        var error = RmStartSession(out var sessionHandle, 0, sessionKeyBuilder);
        if (error != 0)
        {
            return [];
        }

        try
        {
            var files = new[] { fullPath };
            error = RmRegisterResources(sessionHandle, (uint)files.Length, files, 0, IntPtr.Zero, 0, IntPtr.Zero);
            if (error != 0)
            {
                return [];
            }

            var processInfoCount = 0u;
            var processInfoNeeded = 0u;
            var rebootReasons = 0u;
            error = RmGetList(
                sessionHandle,
                out processInfoNeeded,
                ref processInfoCount,
                null!,
                ref rebootReasons);

            if (error != 0 && error != ErrorMoreData)
            {
                return [];
            }

            if (processInfoNeeded == 0)
            {
                return [];
            }

            var processInfo = new RM_PROCESS_INFO[processInfoNeeded];
            processInfoCount = processInfoNeeded;
            error = RmGetList(
                sessionHandle,
                out processInfoNeeded,
                ref processInfoCount,
                processInfo,
                ref rebootReasons);

            if (error != 0)
            {
                return [];
            }

            var results = new List<LockingProcess>();
            for (var i = 0; i < processInfoCount; i++)
            {
                var info = processInfo[i];
                var pid = info.Process.dwProcessId;
                if (pid <= 0)
                {
                    continue;
                }

                var name = ResolveProcessName(info, pid);
                results.Add(new LockingProcess(pid, name));
            }

            return results;
        }
        finally
        {
            _ = RmEndSession(sessionHandle);
        }
    }

    private static string ResolveProcessName(RM_PROCESS_INFO info, int processId)
    {
        if (!string.IsNullOrWhiteSpace(info.strAppName))
        {
            return info.strAppName.Trim();
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch
        {
            return $"Process {processId}";
        }
    }

    [DllImport("Rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

    [DllImport("Rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint pSessionHandle,
        uint nFiles,
        string[] rgsFilenames,
        uint nApplications,
        IntPtr rgApplications,
        uint nServices,
        IntPtr rgsServiceNames);

    [DllImport("Rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps,
        ref uint lpdwRebootReasons);

    [DllImport("Rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxAppName + 1)]
        public string strAppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxSvcName + 1)]
        public string strServiceShortName;

        public uint ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }
}
