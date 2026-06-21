using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Stackroot.Core.Windows;

public static class ProcessMemoryTools
{
    private const int ProcessVmCounters = 3;
    private static readonly object SnapshotSync = new();
    private static readonly TimeSpan SnapshotTtl = TimeSpan.FromSeconds(3);
    private static List<(int Pid, int ParentPid)>? _cachedProcesses;
    private static DateTimeOffset _cachedProcessesExpiresAt;

    public static IReadOnlyList<int> CollectProcessTree(int rootPid)
    {
        if (rootPid <= 0 || !OperatingSystem.IsWindows())
        {
            return rootPid > 0 ? [rootPid] : [];
        }

        var processes = EnumerateProcesses();
        if (processes.Count == 0)
        {
            return [rootPid];
        }

        var childrenByParent = new Dictionary<int, List<int>>();
        foreach (var (pid, parentPid) in processes)
        {
            if (pid <= 0 || parentPid <= 0)
            {
                continue;
            }

            if (!childrenByParent.TryGetValue(parentPid, out var children))
            {
                children = [];
                childrenByParent[parentPid] = children;
            }

            children.Add(pid);
        }

        var result = new List<int>();
        var pending = new Stack<int>();
        var seen = new HashSet<int>();
        pending.Push(rootPid);

        while (pending.Count > 0)
        {
            var pid = pending.Pop();
            if (!seen.Add(pid))
            {
                continue;
            }

            result.Add(pid);
            if (!childrenByParent.TryGetValue(pid, out var children))
            {
                continue;
            }

            foreach (var childPid in children)
            {
                pending.Push(childPid);
            }
        }

        return result;
    }

    /// <summary>
    /// PIDs whose private working set should be summed for a supervised custom process.
    /// Excludes cmd/powershell/conhost wrappers when a real worker child exists.
    /// </summary>
    public static IReadOnlyList<int> CollectManagedProcessMemoryPids(int rootPid)
    {
        var tree = CollectProcessTree(rootPid);
        if (tree.Count <= 1)
        {
            return tree;
        }

        var workers = tree.Where(pid => !IsShellHostProcess(pid)).ToList();
        return workers.Count > 0 ? workers : tree;
    }

    public static double? GetTaskManagerMemoryMb(int pid)
    {
        if (pid <= 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            process.Refresh();

            if (OperatingSystem.IsWindows()
                && TryGetPrivateWorkingSetBytes(process.Handle, out var privateWorkingSetBytes))
            {
                return Math.Round(privateWorkingSetBytes / (1024d * 1024d), 1);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    public static double? SumTaskManagerMemoryMb(IEnumerable<int> pids)
    {
        var total = 0d;
        var any = false;

        foreach (var pid in pids.Where(static pid => pid > 0).Distinct())
        {
            var memoryMb = GetTaskManagerMemoryMb(pid);
            if (memoryMb is null)
            {
                continue;
            }

            total += memoryMb.Value;
            any = true;
        }

        return any ? Math.Round(total, 1) : null;
    }

    private static bool IsShellHostProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName.Equals("cmd", StringComparison.OrdinalIgnoreCase)
                || process.ProcessName.Equals("powershell", StringComparison.OrdinalIgnoreCase)
                || process.ProcessName.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
                || process.ProcessName.Equals("conhost", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static List<(int Pid, int ParentPid)> EnumerateProcesses()
    {
        lock (SnapshotSync)
        {
            if (_cachedProcesses is not null && _cachedProcessesExpiresAt > DateTimeOffset.UtcNow)
            {
                return _cachedProcesses;
            }

            _cachedProcesses = EnumerateProcessesUncached();
            _cachedProcessesExpiresAt = DateTimeOffset.UtcNow.Add(SnapshotTtl);
            return _cachedProcesses;
        }
    }

    private static List<(int Pid, int ParentPid)> EnumerateProcessesUncached()
    {
        var snapshot = CreateToolhelp32Snapshot(SnapshotFlags.Process, 0);
        if (snapshot.IsInvalid)
        {
            return [];
        }

        using (snapshot)
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry))
            {
                return [];
            }

            var processes = new List<(int Pid, int ParentPid)>();
            do
            {
                processes.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID));
            }
            while (Process32Next(snapshot, ref entry));

            return processes;
        }
    }

    private static bool TryGetPrivateWorkingSetBytes(IntPtr processHandle, out ulong bytes)
    {
        bytes = 0;
        if (processHandle == IntPtr.Zero)
        {
            return false;
        }

        var counters = new PROCESS_MEMORY_COUNTERS_EX2
        {
            cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX2>()
        };

        if (GetProcessMemoryInfo(processHandle, out counters, counters.cb)
            && counters.PrivateWorkingSetSize != UIntPtr.Zero)
        {
            bytes = counters.PrivateWorkingSetSize.ToUInt64();
            return true;
        }

        return TryGetPrivateWorkingSetBytesNt(processHandle, out bytes);
    }

    private static bool TryGetPrivateWorkingSetBytesNt(IntPtr processHandle, out ulong bytes)
    {
        bytes = 0;
        var counters = new VM_COUNTERS_EX2();
        var status = NtQueryInformationProcess(
            processHandle,
            ProcessVmCounters,
            ref counters,
            Marshal.SizeOf<VM_COUNTERS_EX2>(),
            out _);

        if (status != 0 || counters.PrivateWorkingSetSize == 0)
        {
            return false;
        }

        bytes = counters.PrivateWorkingSetSize;
        return true;
    }

    [Flags]
    private enum SnapshotFlags : uint
    {
        Process = 0x00000002
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS_EX2
    {
        public uint cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivateUsage;
        public UIntPtr PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VM_COUNTERS_EX2
    {
        public nuint PeakVirtualSize;
        public nuint VirtualSize;
        public uint PageFaultCount;
        private readonly uint _alignmentPadding;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
        public nuint PrivateWorkingSetSize;
        public nuint SharedCommitUsage;
    }

    private static readonly nint InvalidHandleValue = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeSnapshotHandle CreateToolhelp32Snapshot(SnapshotFlags flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32First(SafeSnapshotHandle snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32Next(SafeSnapshotHandle snapshot, ref PROCESSENTRY32 entry);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(
        IntPtr hProcess,
        out PROCESS_MEMORY_COUNTERS_EX2 ppsmemCounters,
        uint cb);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref VM_COUNTERS_EX2 processInformation,
        int processInformationLength,
        out int returnLength);

    private sealed class SafeSnapshotHandle : SafeHandle
    {
        public SafeSnapshotHandle() : base(IntPtr.Zero, true)
        {
        }

        public override bool IsInvalid => handle == IntPtr.Zero || handle == InvalidHandleValue;

        protected override bool ReleaseHandle() => CloseHandle(handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
