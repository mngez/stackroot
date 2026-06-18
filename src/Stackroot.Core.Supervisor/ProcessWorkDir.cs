using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Supervisor;

public static class ProcessWorkDir
{
    public static string Resolve(GlobalProcess process, string? sitePath = null)
    {
        if (!string.IsNullOrWhiteSpace(process.SiteId) && !string.IsNullOrWhiteSpace(sitePath))
        {
            return sitePath.Trim();
        }

        if (!string.IsNullOrWhiteSpace(process.WorkDir))
        {
            return process.WorkDir.Trim();
        }

        return Environment.CurrentDirectory;
    }
}
