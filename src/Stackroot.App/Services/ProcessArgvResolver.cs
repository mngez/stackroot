using System.IO;
using Stackroot.Core.Windows;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.IO;
using Stackroot.Core.Node;
using Stackroot.Core.Settings;
using Stackroot.Core.Sites.Management;
using Stackroot.Core.Supervisor;
using SiteModel = Stackroot.Core.Sites.Models.Site;

namespace Stackroot.App.Services;

public sealed class ProcessArgvResolver : IGlobalProcessArgvResolver
{
    private readonly InstallRegistryStore _registry;
    private readonly SettingsStore _settingsStore;
    private readonly StackrootPaths _paths;
    private readonly SiteManager _siteManager;
    private readonly INpmTooling? _npmTooling;

    public ProcessArgvResolver(
        InstallRegistryStore registry,
        SettingsStore settingsStore,
        StackrootPaths paths,
        SiteManager siteManager,
        INpmTooling? npmTooling = null)
    {
        _registry = registry;
        _settingsStore = settingsStore;
        _paths = paths;
        _siteManager = siteManager;
        _npmTooling = npmTooling;
    }
    public IReadOnlyList<string> Resolve(GlobalProcess process)
    {
        var argv = process.Argv ?? [];
        if (argv.Count == 0 || process.Runtime == SiteCommandRuntime.Shell)
        {
            return argv;
        }

        if (process.Runtime is SiteCommandRuntime.Php
            or SiteCommandRuntime.Composer
            or SiteCommandRuntime.Npm
            or SiteCommandRuntime.Node
            or SiteCommandRuntime.Python)
        {
            var commandText = NormalizeCommandText(
                FormatCommandDisplay(process.Runtime, argv),
                process);
            if (!string.IsNullOrWhiteSpace(commandText))
            {
                return BuildArgv(
                    process.Runtime,
                    commandText,
                    process.SiteId,
                    ResolveWorkDir(process),
                    process.PhpVersionId);
            }
        }

        if (IsResolvedExecutable(process.Runtime, argv[0]))
        {
            return argv;
        }

        var fallbackText = NormalizeCommandText(
            FormatCommandDisplay(process.Runtime, argv),
            process);
        if (string.IsNullOrWhiteSpace(fallbackText))
        {
            return argv;
        }

        return BuildArgv(
            process.Runtime,
            fallbackText,
            process.SiteId,
            ResolveWorkDir(process),
            process.PhpVersionId);
    }

    public string ResolveWorkDir(GlobalProcess process)
    {
        var site = !string.IsNullOrWhiteSpace(process.SiteId) ? _siteManager.Get(process.SiteId) : null;
        return ProcessWorkDir.Resolve(process, site?.Path);
    }

    public IReadOnlyDictionary<string, string?> BuildEnvironment(GlobalProcess process)
    {
        var site = !string.IsNullOrWhiteSpace(process.SiteId) ? _siteManager.Get(process.SiteId) : null;
        var versionId = ResolveEffectivePhpVersionId(site, process.PhpVersionId);
        var environment = SiteProcessEnvironment.Build(_paths, versionId, _npmTooling);
        return environment.ToDictionary(static pair => pair.Key, static pair => (string?)pair.Value);
    }

    public string FormatDisplayCommandLine(GlobalProcess process, IReadOnlyList<string> resolvedArgv)
    {
        var command = FormatCommandDisplay(process.Runtime, resolvedArgv);
        if (process.Runtime != SiteCommandRuntime.Php || string.IsNullOrWhiteSpace(command))
        {
            return command;
        }

        var site = !string.IsNullOrWhiteSpace(process.SiteId) ? _siteManager.Get(process.SiteId) : null;
        var versionId = ResolveEffectivePhpVersionId(site, process.PhpVersionId);
        var alias = !string.IsNullOrWhiteSpace(versionId)
            ? PhpRuntimeAliases.AliasForPackageId(versionId) ?? "php"
            : "php";

        return $"{alias} {command}";
    }

    public string NormalizeUserCommand(SiteCommandRuntime runtime, string commandText, string? fromPreset = null)
    {
        if (runtime == SiteCommandRuntime.Shell || string.IsNullOrWhiteSpace(commandText))
        {
            return commandText;
        }

        return NormalizeCommandText(
            commandText,
            new GlobalProcess
            {
                Runtime = runtime,
                FromPreset = fromPreset
            });
    }

    public List<string> BuildArgv(
        SiteCommandRuntime runtime,
        string commandText,
        string? siteId,
        string? workDir = null,
        string? phpVersionId = null)
    {
        var trimmed = commandText.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return [];
        }

        var site = !string.IsNullOrWhiteSpace(siteId) ? _siteManager.Get(siteId) : null;
        var args = runtime == SiteCommandRuntime.Shell
            ? [trimmed]
            : trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        return runtime switch
        {
            SiteCommandRuntime.Shell => BuildShellArgv(trimmed),
            SiteCommandRuntime.Php => BuildPhpArgv(site, args, workDir, phpVersionId),
            SiteCommandRuntime.Composer => BuildComposerArgv(site, args, workDir, phpVersionId),
            SiteCommandRuntime.Npm => BuildNpmArgv(args),
            SiteCommandRuntime.Node => BuildNodeArgv(args),
            SiteCommandRuntime.Python => BuildPythonArgv(trimmed),
            _ => BuildShellArgv(trimmed)
        };
    }

    public static string FormatCommandDisplay(SiteCommandRuntime runtime, IReadOnlyList<string> argv)
    {
        if (argv.Count == 0)
        {
            return string.Empty;
        }

        if (runtime == SiteCommandRuntime.Shell)
        {
            if (argv.Count >= 3 &&
                argv[0].EndsWith("cmd.exe", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(argv[1], "/c", StringComparison.OrdinalIgnoreCase))
            {
                return string.Join(' ', argv.Skip(2));
            }

            return string.Join(' ', argv);
        }

        var skip = runtime switch
        {
            SiteCommandRuntime.Php when argv.Count > 0 && LooksLikePhpShim(argv[0]) => 1,
            SiteCommandRuntime.Php when argv.Count > 0
                && argv[0].EndsWith("php.exe", StringComparison.OrdinalIgnoreCase)
                && File.Exists(argv[0]) => SkipPhpPrefix(argv),
            SiteCommandRuntime.Composer when argv.Count > 0 && LooksLikeComposerShim(argv[0]) => 1,
            SiteCommandRuntime.Composer when argv.Count > 0
                && argv[0].EndsWith(".phar", StringComparison.OrdinalIgnoreCase)
                && File.Exists(argv[0]) => SkipComposerPrefix(argv),
            SiteCommandRuntime.Npm when argv.Count > 0 && LooksLikeNpmExecutable(argv[0]) => 1,
            SiteCommandRuntime.Node when argv.Count > 0 && LooksLikeNodeExecutable(argv[0]) => 1,
            _ => 0
        };
        return argv.Count > skip ? string.Join(' ', argv.Skip(skip)) : string.Join(' ', argv);
    }

    private static int SkipPhpPrefix(IReadOnlyList<string> argv)
    {
        for (var index = 1; index < argv.Count; index++)
        {
            if (string.Equals(argv[index], "-c", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            return index;
        }

        return argv.Count;
    }

    private static int SkipComposerPrefix(IReadOnlyList<string> argv)
    {
        for (var index = 1; index < argv.Count; index++)
        {
            if (string.Equals(argv[index], "-c", StringComparison.OrdinalIgnoreCase))
            {
                index++;
                continue;
            }

            return index;
        }

        return argv.Count;
    }

    private static bool LooksLikeNpmExecutable(string executable) =>
        executable.EndsWith("npm.cmd", StringComparison.OrdinalIgnoreCase)
        || executable.EndsWith("npm.exe", StringComparison.OrdinalIgnoreCase)
        || (executable.EndsWith("npm", StringComparison.OrdinalIgnoreCase) && File.Exists(executable));

    private static bool LooksLikeNodeExecutable(string executable) =>
        executable.EndsWith("node.exe", StringComparison.OrdinalIgnoreCase)
        || (executable.EndsWith("node", StringComparison.OrdinalIgnoreCase) && File.Exists(executable));

    private static string NormalizeCommandText(string commandText, GlobalProcess process)
    {
        if (string.IsNullOrWhiteSpace(commandText))
        {
            return commandText;
        }

        if (process.Runtime != SiteCommandRuntime.Php)
        {
            return commandText;
        }

        if (commandText.Contains("artisan", StringComparison.OrdinalIgnoreCase))
        {
            return commandText;
        }

        var firstToken = commandText.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return LooksLikeArtisanSubcommand(firstToken)
            ? $"artisan {commandText}"
            : commandText;
    }

    private static bool LooksLikeArtisanSubcommand(string token) =>
        token.StartsWith("queue:", StringComparison.OrdinalIgnoreCase)
        || token.StartsWith("schedule:", StringComparison.OrdinalIgnoreCase)
        || token.StartsWith("migrate", StringComparison.OrdinalIgnoreCase)
        || token.StartsWith("optimize:", StringComparison.OrdinalIgnoreCase)
        || token.StartsWith("cache:", StringComparison.OrdinalIgnoreCase)
        || token.StartsWith("config:", StringComparison.OrdinalIgnoreCase)
        || token.StartsWith("route:", StringComparison.OrdinalIgnoreCase)
        || token.StartsWith("view:", StringComparison.OrdinalIgnoreCase)
        || token.StartsWith("storage:", StringComparison.OrdinalIgnoreCase)
        || string.Equals(token, "horizon", StringComparison.OrdinalIgnoreCase)
        || string.Equals(token, "about", StringComparison.OrdinalIgnoreCase)
        || string.Equals(token, "tinker", StringComparison.OrdinalIgnoreCase);

    private static bool IsResolvedExecutable(SiteCommandRuntime runtime, string executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return false;
        }

        if (Path.IsPathRooted(executable) && File.Exists(executable))
        {
            return true;
        }

        return runtime switch
        {
            SiteCommandRuntime.Php => LooksLikePhpShim(executable) && File.Exists(executable)
                || executable.EndsWith("php.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(executable),
            SiteCommandRuntime.Composer => LooksLikeComposerShim(executable) && File.Exists(executable)
                || executable.EndsWith(".phar", StringComparison.OrdinalIgnoreCase) && File.Exists(executable),
            SiteCommandRuntime.Npm => executable.EndsWith("npm.cmd", StringComparison.OrdinalIgnoreCase)
                || executable.EndsWith("npm", StringComparison.OrdinalIgnoreCase),
            SiteCommandRuntime.Node => executable.EndsWith("node.exe", StringComparison.OrdinalIgnoreCase)
                || executable.EndsWith("node", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static List<string> BuildShellArgv(string commandText)
    {
        var parts = commandText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && File.Exists(parts[0]))
        {
            return parts.ToList();
        }

        return ["cmd.exe", "/c", commandText];
    }

    private List<string> BuildPhpArgv(
        SiteModel? site,
        IReadOnlyList<string> args,
        string? workDir,
        string? phpVersionId)
    {
        var versionId = ResolveEffectivePhpVersionId(site, phpVersionId)
            ?? throw new InvalidOperationException("No PHP version is selected.");
        var projectPath = ResolveProjectPath(site, workDir)
            ?? throw new InvalidOperationException("Project folder is required for PHP processes.");

        var phpExe = StackrootPhpCommands.ResolvePhpExecutable(_registry, versionId);
        var argv = new List<string> { phpExe };
        argv.AddRange(StackrootPhpCommands.BuildArtisanArguments(projectPath, args));
        return argv;
    }

    private List<string> BuildComposerArgv(
        SiteModel? site,
        IReadOnlyList<string> args,
        string? workDir,
        string? phpVersionId)
    {
        var versionId = ResolveEffectivePhpVersionId(site, phpVersionId)
            ?? throw new InvalidOperationException("No PHP version is selected.");
        var phpExe = StackrootPhpCommands.ResolvePhpExecutable(_registry, versionId);
        var invocation = ComposerExecutableResolver.Resolve(_registry, phpExe)
            ?? throw new InvalidOperationException(
                "Composer is not available. Install Composer from Tools, or add composer to your system PATH.");

        var argv = new List<string> { invocation.FileName };
        argv.AddRange(invocation.PrefixArguments);
        argv.AddRange(args);
        return argv;
    }

    private List<string> BuildNpmArgv(IReadOnlyList<string> args)
    {
        var npm = ResolveNpmExecutable()
            ?? throw new InvalidOperationException(
                "npm is unavailable — Node is not activated. Go to Node page → install and activate a Node version.");

        var argv = new List<string> { npm };
        argv.AddRange(args);
        return argv;
    }

    private List<string> BuildNodeArgv(IReadOnlyList<string> args)
    {
        var node = ResolveNodeExecutable()
            ?? throw new InvalidOperationException(
                "Node is not activated. Go to Node page → Install nvm, then install and activate a Node version.");

        var argv = new List<string> { node };
        argv.AddRange(args);
        return argv;
    }

    private static List<string> BuildPythonArgv(string commandText)
    {
        var parts = commandText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && File.Exists(parts[0]))
        {
            return parts.ToList();
        }

        var argv = new List<string> { "python" };
        argv.AddRange(parts);
        return argv;
    }

    private static bool LooksLikePhpShim(string executable)
    {
        if (!executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(executable);
        return name.Equals("php", StringComparison.OrdinalIgnoreCase)
            || (name.StartsWith("php", StringComparison.OrdinalIgnoreCase)
                && name.Length > 3
                && char.IsDigit(name[3]));
    }

    private static bool LooksLikeComposerShim(string executable) =>
        executable.EndsWith("composer.cmd", StringComparison.OrdinalIgnoreCase);

    private string? ResolveEffectivePhpVersionId(SiteModel? site, string? phpVersionIdOverride)
    {
        if (!string.IsNullOrWhiteSpace(phpVersionIdOverride))
        {
            return phpVersionIdOverride;
        }

        if (!string.IsNullOrWhiteSpace(site?.PhpVersionId))
        {
            return site.PhpVersionId;
        }

        return _settingsStore.Load().Php.ActiveVersionId;
    }

    private static string? ResolveProjectPath(SiteModel? site, string? workDir) =>
        !string.IsNullOrWhiteSpace(workDir)
            ? workDir.Trim()
            : site?.Path;

    private string? ResolveNpmExecutable() =>
        ResolveFirstExisting(
            Path.Combine(NodePaths.SymlinkPath(_paths), "npm.cmd"),
            Path.Combine(_paths.RuntimeRoot, "bin", "npm.cmd"));

    private string? ResolveNodeExecutable() =>
        ResolveFirstExisting(
            Path.Combine(NodePaths.SymlinkPath(_paths), "node.exe"),
            Path.Combine(_paths.RuntimeRoot, "bin", "node.exe"));

    private static string? ResolveFirstExisting(params string[] candidates) =>
        candidates.FirstOrDefault(File.Exists);
}
