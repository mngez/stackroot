using System.Text;

namespace Stackroot.Core.Observability;

public static class DevelopmentReportLogRotation
{
    public const string LogFileName = "development-report.log";
    public const string LastLogFileName = "development-report.last.log";

    private static int _rotated;

    public static void RotateSessionLog(string logsRoot)
    {
        if (Interlocked.Exchange(ref _rotated, 1) == 1)
        {
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(logsRoot);

        try
        {
            Directory.CreateDirectory(logsRoot);
            var currentPath = Path.Combine(logsRoot, LogFileName);
            var lastPath = Path.Combine(logsRoot, LastLogFileName);

            if (File.Exists(lastPath))
            {
                File.Delete(lastPath);
            }

            if (File.Exists(currentPath))
            {
                File.Copy(currentPath, lastPath, overwrite: true);
            }

            File.WriteAllText(currentPath, string.Empty, Encoding.UTF8);
        }
        catch
        {
            // Best-effort rotation only.
        }
    }

    public static void ResetForTests() => Interlocked.Exchange(ref _rotated, 0);
}
