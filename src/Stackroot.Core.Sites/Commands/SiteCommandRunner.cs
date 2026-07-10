using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
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
    private readonly SiteCommandRunRegistry _runRegistry;

    public SiteCommandRunner(
        StackrootPaths paths,
        InstallRegistryStore registry,
        SettingsStore settingsStore,
        SiteCommandRunRegistry runRegistry,
        INpmTooling? npmTooling = null)
    {
        _paths = paths;
        _registry = registry;
        _settingsStore = settingsStore;
        _runRegistry = runRegistry;
        _npmTooling = npmTooling;
    }

    public bool IsRunning(string logPath) => _runRegistry.IsRunning(logPath);

    public bool TryCancel(string logPath) => _runRegistry.TryCancel(logPath);

    public event EventHandler<SiteCommandCompletedEventArgs>? CommandCompleted
    {
        add => _runRegistry.CommandCompleted += value;
        remove => _runRegistry.CommandCompleted -= value;
    }

    public IReadOnlyList<ActiveSiteCommand> GetActiveCommands(string siteId) => _runRegistry.GetActiveForSite(siteId);

    public SiteCommandResult RunCustomCommand(
        SiteModel site,
        string commandId,
        string commandLine,
        Action<SiteCommandLogStarted>? onLogCreated = null)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);

        return RunSiteShellCommand(
            site,
            commandLine.Trim(),
            $"custom-{SanitizeLogSlug(commandId)}",
            onLogCreated,
            SiteCommandKind.Custom,
            commandId);
    }

    public SiteCommandResult RunQuickAction(SiteModel site, string actionId, Action<SiteCommandLogStarted>? onLogCreated = null)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);

        var action = SiteQuickActionPresets.Get(actionId)
            ?? throw new InvalidOperationException($"Unknown site action: {actionId}");

        var commandLine = BuildQuickActionCommandLine(site, action.Runtime, action.Argv);
        return RunSiteShellCommand(site, commandLine, action.Id, onLogCreated, SiteCommandKind.QuickAction, action.Id);
    }

    private SiteCommandResult RunSiteShellCommand(
        SiteModel site,
        string commandLine,
        string logSlug,
        Action<SiteCommandLogStarted>? onLogCreated,
        SiteCommandKind kind,
        string commandKey)
    {
        if (!Directory.Exists(site.Path))
        {
            throw new DirectoryNotFoundException($"Site folder not found: {site.Path}");
        }

        var logPath = CreateSiteCommandLogFile(site.Id, logSlug);
        onLogCreated?.Invoke(new SiteCommandLogStarted(logPath, commandLine));
        return RunShellCommand(commandLine, site.Path, ResolveSitePhpVersionId(site), logPath, site.Id, kind, commandKey);
    }

    /// <summary>
    /// Runs a shell command the same way as site custom commands: cmd.exe /c, site env, ConPTY capture.
    /// </summary>
    public SiteCommandResult RunShellCommand(
        string commandLine,
        string workingDirectory,
        string? phpVersionId,
        string logPath,
        string? siteId = null,
        SiteCommandKind kind = SiteCommandKind.Custom,
        string? commandKey = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(logPath);

        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"Working directory not found: {workingDirectory}");
        }

        var trimmed = commandLine.Trim();
        var environment = BuildSiteCommandEnvironment(phpVersionId);
        var startedAt = Stopwatch.StartNew();
        var result = RunShellProcess(workingDirectory, trimmed, environment, logPath, siteId, kind, commandKey);
        startedAt.Stop();

        return new SiteCommandResult
        {
            ExitCode = result.ExitCode,
            Stdout = result.Output,
            Stderr = result.Error,
            DurationMs = startedAt.ElapsedMilliseconds,
            CommandLine = trimmed,
            LogPath = logPath
        };
    }

    public string? ResolvePhpVersionId(SiteModel? site) =>
        site is null ? null : ResolveSitePhpVersionId(site);

    public string CreateScheduledTaskLogFile(string taskId)
    {
        var dir = Path.Combine(_paths.LogsRoot, "scheduled");
        Directory.CreateDirectory(dir);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH-mm-ss-fffZ");
        var path = Path.Combine(dir, $"task-{SanitizeLogSlug(taskId)}-{stamp}.log");
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    private string BuildQuickActionCommandLine(SiteModel site, SiteCommandRuntime runtime, IReadOnlyList<string> argv) =>
        runtime switch
        {
            SiteCommandRuntime.Php => BuildPhpShellCommand(site, argv),
            SiteCommandRuntime.Composer => BuildComposerShellCommand(site, argv),
            SiteCommandRuntime.Npm => JoinShellCommand("npm", argv),
            _ => throw new InvalidOperationException($"Unsupported runtime for quick action: {runtime}")
        };

    private string BuildPhpShellCommand(SiteModel site, IReadOnlyList<string> argv)
    {
        var versionId = ResolveSitePhpVersionId(site);
        if (string.IsNullOrWhiteSpace(versionId))
        {
            throw new InvalidOperationException("No PHP version is selected for this site.");
        }

        _ = StackrootPhpCommands.ResolvePhpExecutable(_registry, versionId);
        var args = StackrootPhpCommands.BuildArtisanArguments(site.Path, argv);
        return JoinShellCommand("php", args);
    }

    private string BuildComposerShellCommand(SiteModel site, IReadOnlyList<string> argv)
    {
        var versionId = ResolveSitePhpVersionId(site);
        if (string.IsNullOrWhiteSpace(versionId))
        {
            throw new InvalidOperationException("No PHP version is selected for this site.");
        }

        var phpExe = StackrootPhpCommands.ResolvePhpExecutable(_registry, versionId);
        var invocation = ComposerExecutableResolver.Resolve(_registry, phpExe)
            ?? throw new InvalidOperationException(
                "Composer is not available. Install Composer from Tools, or add composer to your system PATH.");

        if (invocation.PrefixArguments.Count > 0)
        {
            var args = new List<string>(invocation.PrefixArguments);
            args.AddRange(argv);
            return JoinShellCommand("php", args);
        }

        return JoinShellCommand("composer", argv);
    }

    private string ResolveSitePhpVersionId(SiteModel site)
    {
        var settings = _settingsStore.Load();
        return string.IsNullOrWhiteSpace(site.PhpVersionId)
            ? settings.Php.ActiveVersionId ?? string.Empty
            : site.PhpVersionId;
    }

    private Dictionary<string, string> BuildSiteCommandEnvironment(string? phpVersionId)
    {
        string? phpBinDirectory = null;
        if (!string.IsNullOrWhiteSpace(phpVersionId))
        {
            try
            {
                var phpExe = StackrootPhpCommands.ResolvePhpExecutable(_registry, phpVersionId);
                phpBinDirectory = Path.GetDirectoryName(phpExe);
            }
            catch (InvalidOperationException)
            {
                // PHP not installed — the shell command will fail with a clear error.
            }
        }

        return SiteProcessEnvironment.Build(_paths, phpVersionId, _npmTooling, phpBinDirectory);
    }

    private ProcessResult RunShellProcess(
        string cwd,
        string commandLine,
        IReadOnlyDictionary<string, string> environment,
        string logPath,
        string? siteId,
        SiteCommandKind kind,
        string? commandKey)
    {
        var startInfo = ProcessStreamEncoding.Create("cmd.exe", cwd);
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/s");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(commandLine);

        ApplyEnvironment(startInfo, environment);
        return RunCapturedProcess(startInfo, logPath, "Failed to start site command.", siteId, kind, commandKey, commandLine);
    }

    private static void ApplyEnvironment(ProcessStartInfo startInfo, IReadOnlyDictionary<string, string> environment)
    {
        foreach (var pair in environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }
    }

    private ProcessResult RunCapturedProcess(
        ProcessStartInfo startInfo,
        string logPath,
        string startFailureMessage,
        string? siteId,
        SiteCommandKind kind,
        string? commandKey,
        string? commandLine)
    {
        if (PseudoConsoleCapture.IsSupported && !PseudoConsoleCapture.PreferPipes)
        {
            try
            {
                using var logWriter = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };
                var captured = PseudoConsoleCapture.Run(
                    startInfo,
                    logWriter,
                    process => _runRegistry.Register(logPath, process, siteId, kind, commandKey, commandLine));
                try
                {
                    return new ProcessResult(captured.ExitCode, captured.Output, captured.Error);
                }
                finally
                {
                    _runRegistry.Complete(logPath, captured.ExitCode);
                }
            }
            catch (Exception ex) when (ex is Win32Exception or NotSupportedException or IOException)
            {
                // Fall back to pipe capture with a fresh log file.
            }
        }

        return RunPipeCapturedProcess(startInfo, logPath, startFailureMessage, siteId, kind, commandKey, commandLine);
    }

    private ProcessResult RunPipeCapturedProcess(
        ProcessStartInfo startInfo,
        string logPath,
        string startFailureMessage,
        string? siteId,
        SiteCommandKind kind,
        string? commandKey,
        string? commandLine)
    {
        SiteProcessEnvironment.ApplyRedirectCaptureDefaults(startInfo);

        using var logWriter = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException(startFailureMessage);
        }

        var cancelToken = _runRegistry.Register(logPath, process, siteId, kind, commandKey, commandLine);
        var exitCode = -1;
        try
        {
            var stdoutBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();

            var stdoutTask = Task.Run(() => PumpStream(process.StandardOutput, logWriter, stdoutBuilder));
            var stderrTask = Task.Run(() => PumpStream(process.StandardError, logWriter, stderrBuilder));

            while (!process.HasExited)
            {
                if (cancelToken.IsCancellationRequested)
                {
                    TryKillProcessTree(process);
                    process.WaitForExit(2000);
                    WaitForPumpTasks(stdoutTask, stderrTask, TimeSpan.FromMilliseconds(500));
                    return new ProcessResult(
                        exitCode,
                        stdoutBuilder.ToString(),
                        TrimWithMessage(stderrBuilder, "Command cancelled."));
                }

                process.WaitForExit(50);
            }

            WaitForPumpTasks(stdoutTask, stderrTask, TimeSpan.FromSeconds(30));

            if (cancelToken.IsCancellationRequested)
            {
                return new ProcessResult(
                    exitCode,
                    stdoutBuilder.ToString(),
                    TrimWithMessage(stderrBuilder, "Command cancelled."));
            }

            exitCode = process.ExitCode;
            return new ProcessResult(exitCode, stdoutBuilder.ToString(), stderrBuilder.ToString());
        }
        finally
        {
            _runRegistry.Complete(logPath, exitCode);
        }
    }

    private static void WaitForPumpTasks(Task stdoutTask, Task stderrTask, TimeSpan timeout)
    {
        Task.WaitAny(stdoutTask, stderrTask, Task.Delay(timeout));
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

    private static string TrimWithMessage(StringBuilder builder, string message)
    {
        var text = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(text) ? message : $"{text}{Environment.NewLine}{message}";
    }

    private static void PumpStream(StreamReader reader, StreamWriter logWriter, StringBuilder capture)
    {
        var buffer = new char[4096];
        while (true)
        {
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            var chunk = new string(buffer, 0, read);
            capture.Append(chunk);
            logWriter.Write(chunk);
        }
    }

    private string CreateSiteCommandLogFile(string siteId, string slug)
    {
        var dir = Path.Combine(_paths.LogsRoot, "sites", siteId);
        Directory.CreateDirectory(dir);
        var stamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH-mm-ss-fffZ");
        var path = Path.Combine(dir, $"{slug}-{stamp}.log");
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    private static string SanitizeLogSlug(string value)
    {
        var slug = new string(value.Where(static c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        if (string.IsNullOrWhiteSpace(slug))
        {
            return "cmd";
        }

        return slug.Length <= 32 ? slug : slug[..32];
    }

    private static string JoinShellCommand(string executable, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder(executable);
        foreach (var argument in arguments)
        {
            builder.Append(' ');
            builder.Append(QuoteCmdArgument(argument));
        }

        return builder.ToString();
    }

    private static string QuoteCmdArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }

        if (value.IndexOfAny([' ', '\t', '"', '&', '|', '<', '>']) < 0)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\\\"")}\"";
    }

    private sealed record ProcessResult(int ExitCode, string Output, string Error);
}
