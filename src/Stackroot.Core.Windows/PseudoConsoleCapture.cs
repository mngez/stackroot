using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Stackroot.Core.Windows;

/// <summary>
/// Runs console children on a Windows pseudo-console (ConPTY) so output matches an interactive terminal.
/// Default for site command capture; set STACKROOT_USE_PIPES=1 to force pipe redirect instead.
/// </summary>
public static class PseudoConsoleCapture
{
    public const short DefaultColumns = 120;
    public const short DefaultRows = 40;

    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

    public static bool PreferPipes =>
        string.Equals(Environment.GetEnvironmentVariable("STACKROOT_USE_PIPES"), "1", StringComparison.Ordinal);

    public static CapturedProcessResult Run(
        ProcessStartInfo startInfo,
        TextWriter logWriter,
        Func<Process, CancellationToken> registerCancel,
        short columns = DefaultColumns,
        short rows = DefaultRows)
    {
        if (!IsSupported)
        {
            throw new NotSupportedException("ConPTY requires Windows 10 1809 or later.");
        }

        var commandLine = BuildCommandLine(startInfo);
        var workingDirectory = string.IsNullOrWhiteSpace(startInfo.WorkingDirectory)
            ? Environment.CurrentDirectory
            : startInfo.WorkingDirectory;
        var environmentBlock = BuildEnvironmentBlock(startInfo);

        SafeFileHandle? inputWrite = null;
        SafeFileHandle? outputRead = null;
        IntPtr pseudoConsole = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero;
        Process? process = null;
        IntPtr processHandle = IntPtr.Zero;
        Task? readTask = null;
        StreamReader? outputReader = null;
        FileStream? outputStream = null;

        try
        {
            CreateInheritPipe(out var inputRead, out inputWrite);
            CreateInheritPipe(out outputRead, out var outputWrite);

            var size = new Coord { X = columns, Y = rows };
            var hr = CreatePseudoConsole(size, inputRead.DangerousGetHandle(), outputWrite.DangerousGetHandle(), 0, out pseudoConsole);
            inputRead.Dispose();
            outputWrite.Dispose();

            if (hr != 0)
            {
                throw new Win32Exception(hr, "CreatePseudoConsole failed.");
            }

            var startupInfo = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    cb = Marshal.SizeOf<StartupInfoEx>(),
                    dwFlags = STARTF_USESTDHANDLES,
                    wShowWindow = SW_HIDE,
                    hStdInput = IntPtr.Zero,
                    hStdOutput = IntPtr.Zero,
                    hStdError = IntPtr.Zero
                }
            };

            var attributeListSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
            attributeList = Marshal.AllocHGlobal(attributeListSize.ToInt32());
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            startupInfo.lpAttributeList = attributeList;
            if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    pseudoConsole,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var commandLineBuffer = new StringBuilder(commandLine);
            var creationFlags = EXTENDED_STARTUPINFO_PRESENT;
            if (environmentBlock is not null)
            {
                creationFlags |= CREATE_UNICODE_ENVIRONMENT;
            }

            if (!CreateProcessW(
                    null,
                    commandLineBuffer,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    creationFlags,
                    environmentBlock,
                    workingDirectory,
                    ref startupInfo,
                    out var processInfo))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            processHandle = processInfo.hProcess;
            CloseHandle(processInfo.hThread);
            process = Process.GetProcessById(processInfo.dwProcessId);
            var cancelToken = registerCancel(process);

            var outputBuilder = new StringBuilder();
            outputStream = new FileStream(outputRead, FileAccess.Read);
            outputReader = new StreamReader(outputStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            readTask = Task.Run(() => PumpOutput(outputReader, logWriter, outputBuilder));

            var (exitCode, error) = WaitForProcessExit(processHandle, cancelToken, process);
            var cancelled = cancelToken.IsCancellationRequested;

            inputWrite?.Dispose();
            inputWrite = null;

            if (cancelled && pseudoConsole != IntPtr.Zero)
            {
                ClosePseudoConsole(pseudoConsole);
                pseudoConsole = IntPtr.Zero;
            }

            CloseOutputPump(ref outputReader, ref outputStream);
            DrainReadTask(readTask);
            readTask = null;

            return new CapturedProcessResult(exitCode, outputBuilder.ToString(), error);
        }
        finally
        {
            DrainReadTask(readTask);
            readTask = null;

            CloseOutputPump(ref outputReader, ref outputStream);
            process?.Dispose();
            inputWrite?.Dispose();
            // outputStream owns the read side of the pipe — do not dispose outputRead again.

            if (processHandle != IntPtr.Zero)
            {
                CloseHandle(processHandle);
            }

            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            if (pseudoConsole != IntPtr.Zero)
            {
                ClosePseudoConsole(pseudoConsole);
            }
        }
    }

    private static void PumpOutput(StreamReader reader, TextWriter logWriter, StringBuilder outputBuilder)
    {
        var buffer = new char[4096];
        try
        {
            while (true)
            {
                var read = reader.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                var chunk = new string(buffer, 0, read);
                lock (outputBuilder)
                {
                    outputBuilder.Append(chunk);
                }

                logWriter.Write(chunk);
            }
        }
        catch (ObjectDisposedException)
        {
            // Process ended and handles were closed — expected during cancel/shutdown.
        }
    }

    private static void CloseOutputPump(ref StreamReader? outputReader, ref FileStream? outputStream)
    {
        try
        {
            outputReader?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        outputReader = null;

        try
        {
            outputStream?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        outputStream = null;
    }

    private static void DrainReadTask(Task? readTask)
    {
        if (readTask is null || readTask.IsCompleted)
        {
            return;
        }

        try
        {
            readTask.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch (AggregateException ex) when (ex.InnerException is ObjectDisposedException)
        {
            // Reader closed after process exit or cancel.
        }
    }

    private static string BuildCommandLine(ProcessStartInfo startInfo)
    {
        var builder = new StringBuilder();
        builder.Append(QuoteCommandLineArgument(startInfo.FileName));
        if (startInfo.ArgumentList.Count > 0)
        {
            foreach (var argument in startInfo.ArgumentList)
            {
                builder.Append(' ');
                builder.Append(QuoteCommandLineArgument(argument));
            }
        }
        else if (!string.IsNullOrWhiteSpace(startInfo.Arguments))
        {
            builder.Append(' ');
            builder.Append(startInfo.Arguments);
        }

        return builder.ToString();
    }

    private static string QuoteCommandLineArgument(string argument)
    {
        if (string.IsNullOrEmpty(argument))
        {
            return "\"\"";
        }

        var needsQuotes = false;
        for (var i = 0; i < argument.Length; i++)
        {
            var ch = argument[i];
            if (ch is ' ' or '\t' or '"')
            {
                needsQuotes = true;
                break;
            }
        }

        if (!needsQuotes)
        {
            return argument;
        }

        var quoted = new StringBuilder('"');
        for (var i = 0; i < argument.Length; i++)
        {
            var backslashes = 0;
            while (i < argument.Length && argument[i] == '\\')
            {
                backslashes++;
                i++;
            }

            if (i >= argument.Length)
            {
                quoted.Append('\\', backslashes * 2);
                break;
            }

            if (argument[i] == '"')
            {
                quoted.Append('\\', backslashes * 2 + 1);
                quoted.Append('"');
            }
            else
            {
                quoted.Append('\\', backslashes);
                quoted.Append(argument[i]);
            }
        }

        quoted.Append('"');
        return quoted.ToString();
    }

    private static char[]? BuildEnvironmentBlock(ProcessStartInfo startInfo)
    {
        if (startInfo.Environment.Count == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var pair in startInfo.Environment.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (pair.Value is null)
            {
                continue;
            }

            builder.Append(pair.Key);
            builder.Append('=');
            builder.Append(pair.Value);
            builder.Append('\0');
        }

        builder.Append('\0');
        return builder.ToString().ToCharArray();
    }

    private static (int ExitCode, string Error) WaitForProcessExit(
        IntPtr processHandle,
        CancellationToken cancelToken,
        Process process)
    {
        while (true)
        {
            if (cancelToken.IsCancellationRequested)
            {
                TryKillProcessTree(process);
                return (-1, "Command cancelled.");
            }

            if (!GetExitCodeProcess(processHandle, out var exitCode))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (exitCode != StillActive)
            {
                if (cancelToken.IsCancellationRequested)
                {
                    return (-1, "Command cancelled.");
                }

                return ((int)exitCode, string.Empty);
            }

            WaitForSingleObject(processHandle, 50);
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    private const uint StillActive = 259;
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const short SW_HIDE = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    private static void CreateInheritPipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe)
    {
        var attributes = new SecurityAttributes
        {
            nLength = Marshal.SizeOf<SecurityAttributes>(),
            bInheritHandle = true
        };

        if (!CreatePipe(out readPipe, out writePipe, ref attributes, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(
        out SafeFileHandle hReadPipe,
        out SafeFileHandle hWritePipe,
        ref SecurityAttributes lpPipeAttributes,
        uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(
        Coord size,
        IntPtr hInput,
        IntPtr hOutput,
        uint dwFlags,
        out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr attribute,
        IntPtr lpValue,
        IntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        char[]? lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfoEx lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}

public readonly record struct CapturedProcessResult(int ExitCode, string Output, string Error);
