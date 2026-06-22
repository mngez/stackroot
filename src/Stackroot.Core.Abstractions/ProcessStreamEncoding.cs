using System.Diagnostics;
using System.Text;

namespace Stackroot.Core.Abstractions;

/// <summary>
/// Default <see cref="ProcessStartInfo"/> for hidden subprocesses with UTF-8 stream capture.
/// </summary>
public static class ProcessStreamEncoding
{
    /// <summary>
    /// Hidden process with UTF-8 stdout and stderr capture.
    /// </summary>
    public static ProcessStartInfo Create(
        string fileName,
        string? workingDirectory = null,
        bool redirectStdin = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        if (redirectStdin)
        {
            startInfo.RedirectStandardInput = true;
            startInfo.StandardInputEncoding = Encoding.UTF8;
        }

        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
        return startInfo;
    }

    /// <summary>
    /// Hidden process with UTF-8 stdout capture only (no stderr redirect).
    /// </summary>
    public static ProcessStartInfo CreateStdoutOnly(
        string fileName,
        string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        return startInfo;
    }
}
