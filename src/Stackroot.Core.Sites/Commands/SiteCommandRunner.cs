using System.Diagnostics;
using System.Text;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Node;
using Stackroot.Core.Settings;
using Stackroot.Core.Sites.Models;
using Stackroot.Core.Windows;
using SiteModel = Stackroot.Core.Sites.Models.Site;

namespace Stackroot.Core.Sites.Commands;

public sealed class SiteCommandRunner
{
    private readonly StackrootPaths _paths;
    private readonly InstallRegistryStore _registry;
    private readonly SettingsStore _settingsStore;
    private readonly INpmTooling? _npmTooling;

    public SiteCommandRunner(
        StackrootPaths paths,
        InstallRegistryStore registry,
        SettingsStore settingsStore,
        INpmTooling? npmTooling = null)
    {
        _paths = paths;
        _registry = registry;
        _settingsStore = settingsStore;
        _npmTooling = npmTooling;
    }

    public SiteCommandResult RunQuickAction(SiteModel site, string actionId, Action<string>? onLogCreated = null)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);

        var action = SiteQuickActionPresets.Get(actionId)
            ?? throw new InvalidOperationException($"Unknown site action: {actionId}");

        if (!Directory.Exists(site.Path))
        {
            throw new DirectoryNotFoundException($"Site folder not found: {site.Path}");
        }

        var resolved = ResolveCommand(site, action.Runtime, action.Argv);
        var commandLine = BuildCommandLine(resolved.Executable, resolved.Arguments);
        var logPath = CreateLogFile(site.Id, action.Id, commandLine);
        onLogCreated?.Invoke(logPath);
        var startedAt = Stopwatch.StartNew();
        var result = RunProcess(
            resolved.Executable,
            resolved.Arguments,
            resolved.WorkingDirectory,
            resolved.Environment,
            logPath,
            resolved.TimeoutMs);
        startedAt.Stop();

        AppendLogFooter(logPath, result.ExitCode, startedAt.ElapsedMilliseconds);

        return new SiteCommandResult
        {
            ExitCode = result.ExitCode,
            Stdout = result.Output,
            Stderr = result.Error,
            DurationMs = startedAt.ElapsedMilliseconds,
            CommandLine = commandLine,
            LogPath = logPath
        };
    }

    private ResolvedCommand ResolveCommand(SiteModel site, SiteCommandRuntime runtime, IReadOnlyList<string> argv)
    {
        return runtime switch
        {
            SiteCommandRuntime.Php => ResolvePhpCommand(site, argv),
            SiteCommandRuntime.Composer => ResolveComposerCommand(site, argv),
            SiteCommandRuntime.Npm => ResolveNpmCommand(site, argv),
            _ => throw new InvalidOperationException($"Unsupported runtime for quick action: {runtime}")
        };
    }

    private ResolvedCommand ResolvePhpCommand(SiteModel site, IReadOnlyList<string> argv)
    {
        var versionId = ResolveSitePhpVersionId(site);
        if (string.IsNullOrWhiteSpace(versionId))
        {
            throw new InvalidOperationException("No PHP version is selected for this site.");
        }

        var phpExe = StackrootPhpCommands.ResolvePhpExecutable(_registry, versionId);
        var args = StackrootPhpCommands.BuildArtisanArguments(site.Path, argv);
        var environment = BuildSiteCommandEnvironment(versionId);
        var timeoutMs = ResolveCommandTimeoutMs(_settingsStore.Load(), versionId);

        return new ResolvedCommand(phpExe, args, site.Path, environment, timeoutMs);
    }

    private static int ResolveCommandTimeoutMs(AppSettings settings, string versionId)
    {
        if (settings.Php.Versions?.TryGetValue(versionId, out var versionSettings) == true
            && int.TryParse(versionSettings.MaxExecutionTime.Trim().TrimEnd('s', 'S'), out var seconds)
            && seconds > 0)
        {
            return seconds * 1000;
        }

        return 120_000;
    }

    private ResolvedCommand ResolveComposerCommand(SiteModel site, IReadOnlyList<string> argv)
    {
        var versionId = ResolveSitePhpVersionId(site);
        var phpExe = StackrootPhpCommands.ResolvePhpExecutable(_registry, versionId);
        var invocation = ComposerExecutableResolver.Resolve(_registry, phpExe)
            ?? throw new InvalidOperationException(
                "Composer is not available. Install Composer from Tools, or add composer to your system PATH.");

        var args = new List<string>(invocation.PrefixArguments);
        args.AddRange(argv);
        var environment = BuildSiteCommandEnvironment(versionId);
        var timeoutMs = ResolveCommandTimeoutMs(_settingsStore.Load(), versionId);

        return new ResolvedCommand(invocation.FileName, args, site.Path, environment, timeoutMs);
    }

    private ResolvedCommand ResolveNpmCommand(SiteModel site, IReadOnlyList<string> argv)
    {
        var npm = ResolveFirstExisting(
            Path.Combine(NodePaths.SymlinkPath(_paths), "npm.cmd"),
            Path.Combine(NodePaths.SymlinkPath(_paths), "npm"),
            Path.Combine(_paths.RuntimeRoot, "bin", "npm.cmd"));
        if (npm is null)
        {
            throw new InvalidOperationException("npm is unavailable. Activate a Node version first.");
        }

        return new ResolvedCommand(npm, argv.ToList(), site.Path, BuildSiteCommandEnvironment(null), 600_000);
    }

    private string ResolveSitePhpVersionId(SiteModel site)
    {
        var settings = _settingsStore.Load();
        return string.IsNullOrWhiteSpace(site.PhpVersionId)
            ? settings.Php.ActiveVersionId ?? string.Empty
            : site.PhpVersionId;
    }

    private Dictionary<string, string> BuildSiteCommandEnvironment(string? phpVersionId) =>
        SiteProcessEnvironment.Build(_paths, phpVersionId, _npmTooling);

    private static ProcessResult RunProcess(
        string executable,
        IReadOnlyList<string> args,
        string cwd,
        IReadOnlyDictionary<string, string> environment,
        string logPath,
        int timeoutMs)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        foreach (var pair in environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start command: {executable}");
        }

        using var logWriter = new StreamWriter(logPath, append: true, Encoding.UTF8) { AutoFlush = true };
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        var stdoutTask = Task.Run(() => PumpStream(process.StandardOutput, logWriter, stdoutBuilder));
        var stderrTask = Task.Run(() => PumpStream(process.StandardError, logWriter, stderrBuilder, stderr: true));

        if (!process.WaitForExit(timeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort.
            }

            Task.WaitAll(stdoutTask, stderrTask);
            logWriter.WriteLine("[stderr] Command timed out and was stopped.");
            return new ProcessResult(-1, stdoutBuilder.ToString().Trim(), TrimWithMessage(stderrBuilder, "Command timed out."));
        }

        Task.WaitAll(stdoutTask, stderrTask);

        return new ProcessResult(process.ExitCode, stdoutBuilder.ToString().Trim(), stderrBuilder.ToString().Trim());
    }

    private static string TrimWithMessage(StringBuilder builder, string message)
    {
        var text = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(text) ? message : $"{text}{Environment.NewLine}{message}";
    }

    private static void PumpStream(StreamReader reader, StreamWriter logWriter, StringBuilder capture, bool stderr = false)
    {
        while (true)
        {
            var line = reader.ReadLine();
            if (line is null)
            {
                break;
            }

            capture.AppendLine(line);
            if (stderr)
            {
                logWriter.WriteLine($"[stderr] {line}");
            }
            else
            {
                logWriter.WriteLine(line);
            }
        }
    }

    private string CreateLogFile(string siteId, string actionId, string commandLine)
    {
        var dir = Path.Combine(_paths.LogsRoot, "sites", siteId);
        Directory.CreateDirectory(dir);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH-mm-ss-fffZ");
        var path = Path.Combine(dir, $"{actionId}-{stamp}.log");
        File.WriteAllText(
            path,
            new StringBuilder()
                .AppendLine($"# {commandLine}")
                .AppendLine("# running…")
                .AppendLine()
                .ToString(),
            Encoding.UTF8);
        return path;
    }

    private static void AppendLogFooter(string logPath, int exitCode, long durationMs)
    {
        File.AppendAllText(
            logPath,
            $"{Environment.NewLine}# exit {exitCode} · {durationMs}ms{Environment.NewLine}",
            Encoding.UTF8);
    }

    private static string BuildCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        return $"{executable} {string.Join(' ', arguments)}".Trim();
    }

    private static string? ResolveFirstExisting(params string[] candidates)
    {
        return candidates.FirstOrDefault(File.Exists);
    }

    private sealed record ResolvedCommand(
        string Executable,
        IReadOnlyList<string> Arguments,
        string WorkingDirectory,
        IReadOnlyDictionary<string, string> Environment,
        int TimeoutMs = 120_000);
    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
