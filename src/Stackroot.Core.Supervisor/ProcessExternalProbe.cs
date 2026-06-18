using Stackroot.Core.Abstractions;
using Stackroot.Core.Windows;

namespace Stackroot.Core.Supervisor;

internal static class ProcessExternalProbe
{
    public static bool TryDetectRunning(
        GlobalProcess process,
        IReadOnlyList<string> argv,
        string resolvedCwd,
        out int pid)
    {
        pid = 0;
        if (argv.Count == 0)
        {
            return false;
        }

        var port = ResolveExpectedPort(process.Runtime, argv, resolvedCwd);
        if (port is null or <= 0)
        {
            return false;
        }

        foreach (var candidatePid in ProcessPortTools.FindPidsListeningOnPort(port.Value))
        {
            if (candidatePid <= 0)
            {
                continue;
            }

            pid = candidatePid;
            return true;
        }

        return false;
    }

    private static int? ResolveExpectedPort(
        SiteCommandRuntime runtime,
        IReadOnlyList<string> argv,
        string resolvedCwd)
    {
        if (runtime == SiteCommandRuntime.Shell
            && argv.Count >= 3
            && argv[0].EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase)
            && string.Equals(argv[1], "/c", StringComparison.OrdinalIgnoreCase))
        {
            var shellCommand = argv[2];
            var parts = shellCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return null;
            }

            var parsed = ProcessPortTools.TryParsePortFromArguments(parts);
            if (parsed is > 0)
            {
                return parsed;
            }

            var executable = ProcessAvailability.ResolveShellCommandExecutable(shellCommand, resolvedCwd);
            if (executable is not null)
            {
                return ProcessPortTools.TryResolveListenPort(executable, parts.Skip(1).ToList());
            }

            return null;
        }

        return ProcessPortTools.TryResolveListenPort(argv[0], argv.Skip(1).ToList());
    }
}
