using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Supervisor;

internal static class ProcessAvailability
{
    public static bool IsAvailable(GlobalProcess process, IReadOnlyList<string> argv, string resolvedCwd)
    {
        if (argv.Count == 0 || string.IsNullOrWhiteSpace(argv[0]) || !Directory.Exists(resolvedCwd))
        {
            return false;
        }

        if (IsKnownPathExecutable(argv[0]))
        {
            return ShellCommandLooksRunnable(process.Runtime, argv, resolvedCwd);
        }

        if (Path.IsPathRooted(argv[0]))
        {
            return File.Exists(argv[0]);
        }

        return File.Exists(Path.Combine(resolvedCwd, argv[0]));
    }

    private static bool ShellCommandLooksRunnable(
        SiteCommandRuntime runtime,
        IReadOnlyList<string> argv,
        string resolvedCwd)
    {
        if (runtime != SiteCommandRuntime.Shell
            || argv.Count < 3
            || !argv[0].EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(argv[1], "/c", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ResolveShellCommandExecutable(argv[2], resolvedCwd) is not null;
    }

    internal static string? ResolveShellCommandExecutable(string command, string resolvedCwd)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        var token = parts[0].Trim('"');
        if (Path.IsPathRooted(token))
        {
            return File.Exists(token) ? token : null;
        }

        var localExecutable = Path.GetFullPath(Path.Combine(resolvedCwd, token));
        if (File.Exists(localExecutable))
        {
            return localExecutable;
        }

        if (!IsKnownShellCommand(token))
        {
            return null;
        }

        if (parts.Length >= 2)
        {
            var target = parts[1].Trim('"');
            if (!target.StartsWith('-') && !IsKnownShellCommand(target))
            {
                var scriptPath = Path.IsPathRooted(target)
                    ? target
                    : Path.GetFullPath(Path.Combine(resolvedCwd, NormalizeRelativeToken(target)));
                if (!File.Exists(scriptPath))
                {
                    return null;
                }
            }
        }

        return token;
    }

    private static string NormalizeRelativeToken(string token)
    {
        if (token.StartsWith("./", StringComparison.Ordinal))
        {
            return token[2..];
        }

        if (token.StartsWith(".\\", StringComparison.Ordinal))
        {
            return token[2..];
        }

        return token;
    }

    private static bool IsKnownShellCommand(string token)
    {
        var name = Path.GetFileName(token);
        return name.Equals("node", StringComparison.OrdinalIgnoreCase)
            || name.Equals("node.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("npm", StringComparison.OrdinalIgnoreCase)
            || name.Equals("npm.cmd", StringComparison.OrdinalIgnoreCase)
            || name.Equals("npm.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("npx", StringComparison.OrdinalIgnoreCase)
            || name.Equals("npx.cmd", StringComparison.OrdinalIgnoreCase)
            || name.Equals("python", StringComparison.OrdinalIgnoreCase)
            || name.Equals("python.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("php", StringComparison.OrdinalIgnoreCase)
            || name.Equals("php.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownPathExecutable(string executable)
    {
        var name = Path.GetFileName(executable);
        return name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("python.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("python", StringComparison.OrdinalIgnoreCase)
            || name.Equals("npm.cmd", StringComparison.OrdinalIgnoreCase)
            || name.Equals("npm.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("npm", StringComparison.OrdinalIgnoreCase)
            || name.Equals("node.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("node", StringComparison.OrdinalIgnoreCase);
    }
}
