using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Nginx;
using Stackroot.Core.Services;
using Stackroot.Core.Settings;
using Stackroot.Core.Supervisor;
using Stackroot.Core.Windows;

namespace Stackroot.Core.AdminTools;

public sealed class MailpitManager
{
    public const string WebPath = "mail";
    public const string WebRoot = "/mail";
    public const string AppDomainNginxConfFileName = "stackroot-app.conf";

    private const string ProcessId = "mailpit";

    private readonly ProcessSupervisor _supervisor;
    private readonly SettingsStore _settingsStore;
    private readonly InstallRegistryStore _registryStore;
    private readonly StackrootPaths _paths;
    private readonly IProcessJobManager _jobManager;
    private readonly AppDomainConfigWriter _appDomainConfigWriter;
    private readonly IDiagnosticsReporter _diagnostics;

    public event EventHandler? StatusChanged;

    public MailpitManager(
        ProcessSupervisor supervisor,
        SettingsStore settingsStore,
        InstallRegistryStore registryStore,
        StackrootPaths paths,
        IProcessJobManager jobManager,
        AppDomainConfigWriter appDomainConfigWriter,
        IDiagnosticsReporter? diagnostics = null)
    {
        _supervisor = supervisor;
        _settingsStore = settingsStore;
        _registryStore = registryStore;
        _paths = paths;
        _jobManager = jobManager;
        _appDomainConfigWriter = appDomainConfigWriter;
        _diagnostics = diagnostics ?? NoOpDiagnosticsReporter.Instance;
    }

    public async Task<MailpitStatus> StartAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settingsStore.Load();
        if (!settings.Mailpit.Enabled)
        {
            var status = await GetStatusAsync(cancellationToken);
            return status with { Message = "Mailpit is disabled in settings." };
        }

        var installed = _registryStore.GetById(settings.Mailpit.PackageId);
        if (installed is null)
        {
            var status = await GetStatusAsync(cancellationToken);
            return status with { Message = "Install Mailpit package first." };
        }

        var executable = ResolveExecutable(installed.InstallPath);
        if (executable is null)
        {
            var status = await GetStatusAsync(cancellationToken);
            return status with { Message = "mailpit executable not found." };
        }

        var databasePath = ResolveDatabasePath();

        _supervisor.Start(new ProcessRunTarget(
            Scope(),
            "Mailpit",
            executable,
            [
                "--smtp", $"127.0.0.1:{settings.Mailpit.SmtpPort}",
                "--listen", $"127.0.0.1:{settings.Mailpit.WebPort}",
                "--webroot", WebRoot,
                "--database", databasePath
            ],
            Path.GetDirectoryName(executable) ?? installed.InstallPath,
            Supervised: true));

        var ready = await PortProbe.WaitForPortAsync("127.0.0.1", settings.Mailpit.WebPort, attempts: 20, delayMs: 250, cancellationToken);
        if (!ready)
        {
            var status = await GetStatusAsync(cancellationToken);
            return status with { Message = "Mailpit started but web port did not open." };
        }

        _appDomainConfigWriter.Write();
        await TryReloadNginxAsync(cancellationToken);
        var startedStatus = await GetStatusAsync(cancellationToken);
        NotifyStatusChanged();
        return startedStatus;
    }

    public async Task<MailpitStatus> EnsureAutoStartAsync(CancellationToken cancellationToken = default)
    {
        await ApplyAsync(cancellationToken).ConfigureAwait(false);
        var settings = _settingsStore.Load();
        var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var action = MailpitAutoStartDecision.Decide(
            settings.Mailpit.Enabled,
            settings.Mailpit.AutoStart,
            status.Installed,
            status.Running);

        switch (action)
        {
            case MailpitAutoStartAction.SkipDisabled:
                _diagnostics.LogActivity("Mailpit", "Auto-start skipped (disabled in settings)");
                return status;
            case MailpitAutoStartAction.SkipNotInstalled:
                _diagnostics.LogActivity("Mailpit", "Auto-start skipped (package not installed)");
                return status with
                {
                    Message = "Mailpit is not installed; auto-start is waiting for installation."
                };
            case MailpitAutoStartAction.SkipAlreadyRunning:
                _diagnostics.LogActivity("Mailpit", "Auto-start skipped (already running)");
                NotifyStatusChanged();
                return status;
        }

        _diagnostics.LogActivity("Mailpit", "Auto-starting Mailpit");
        var started = await StartAsync(cancellationToken).ConfigureAwait(false);
        if (started.Running)
        {
            _diagnostics.LogActivity("Mailpit", $"Auto-start finished (Running, web {started.WebUrl})");
        }
        else if (!string.IsNullOrWhiteSpace(started.Message))
        {
            _diagnostics.LogUserError("Mailpit", $"Auto-start did not complete: {started.Message}");
        }
        else
        {
            _diagnostics.LogUserError("Mailpit", "Auto-start did not complete — Mailpit is not running");
        }

        NotifyStatusChanged();
        return started;
    }

    public async Task ApplyAsync(CancellationToken cancellationToken = default)
    {
        _appDomainConfigWriter.Write();
        await TryReloadNginxAsync(cancellationToken);
    }

    public Task StopForShutdownAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _supervisor.Stop(Scope());
        NotifyStatusChanged();
        return Task.CompletedTask;
    }

    public async Task<MailpitStatus> StopAsync(CancellationToken cancellationToken = default)
    {
        _supervisor.Stop(Scope());
        var status = await GetStatusAsync(cancellationToken);
        NotifyStatusChanged();
        return status;
    }

    public async Task<MailpitStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settingsStore.Load();
        var snapshot = _supervisor.GetStatus(Scope());
        var running = snapshot is not null && snapshot.Pid is not null;
        var webOpen = await PortProbe.IsPortOpenAsync("127.0.0.1", settings.Mailpit.WebPort);

        cancellationToken.ThrowIfCancellationRequested();
        var appDomain = ResolveAppDomain(settings);
        var nginxPort = ResolveNginxPort(settings);
        var isRunning = running || webOpen;

        return new MailpitStatus
        {
            Enabled = settings.Mailpit.Enabled,
            Installed = _registryStore.IsInstalled(settings.Mailpit.PackageId),
            Running = isRunning,
            Status = isRunning ? ProcessStatus.Running : ProcessStatus.Stopped,
            Pid = snapshot?.Pid,
            WebUrl = isRunning && settings.Mailpit.Enabled
                ? await ResolveWebUrlAsync(appDomain, nginxPort, settings.Mailpit.WebPort)
                : string.Empty,
            OpenLabel = $"{appDomain}/{WebPath}",
            SmtpEndpoint = $"127.0.0.1:{settings.Mailpit.SmtpPort}",
            Message = snapshot?.Message
        };
    }

    private async Task TryReloadNginxAsync(CancellationToken cancellationToken)
    {
        var settings = _settingsStore.Load();
        if (!settings.Services.TryGetValue(ServiceId.Nginx, out var nginxSettings))
        {
            return;
        }

        var definition = SettingsDefaults.ServiceDefinitions.First(d => d.Id == ServiceId.Nginx);
        var packageId = nginxSettings.PackageId ?? definition.PackageId;
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return;
        }

        var installed = _registryStore.GetById(packageId);
        if (installed is null)
        {
            return;
        }

        var host = string.IsNullOrWhiteSpace(nginxSettings.Host) ? "127.0.0.1" : nginxSettings.Host;
        var port = nginxSettings.Port > 0 ? nginxSettings.Port : definition.DefaultPort;
        _ = await NginxControl.ReloadNginxAsync(
            _paths,
            installed.InstallPath,
            _jobManager,
            host,
            port,
            cancellationToken);
    }

    private static async Task<string> ResolveWebUrlAsync(string appDomain, int nginxPort, int webPort)
    {
        try
        {
            if (await PortProbe.IsPortOpenAsync("127.0.0.1", nginxPort))
            {
                return BuildPublicUrl(appDomain, nginxPort);
            }
        }
        catch
        {
        }

        return $"http://localhost:{webPort}/mail/";
    }

    private static string BuildPublicUrl(string appDomain, int nginxPort)
    {
        var portSuffix = nginxPort == 80 ? string.Empty : $":{nginxPort}";
        return $"http://{appDomain}{portSuffix}/{WebPath}/";
    }

    private static string ResolveAppDomain(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.General.AppDomain) ? "stackroot.test" : settings.General.AppDomain.Trim();

    private static int ResolveNginxPort(AppSettings settings)
    {
        if (settings.Services.TryGetValue(ServiceId.Nginx, out var nginx) && nginx.Port > 0)
        {
            return nginx.Port;
        }

        return SettingsDefaults.DefaultServices()[ServiceId.Nginx].Port;
    }

    private static ProcessScope Scope() =>
        new()
        {
            Type = ProcessScopeType.Global,
            ProcessId = ProcessId
        };

    private string ResolveDatabasePath()
    {
        var databaseDir = Path.Combine(_paths.DataRoot, "data", "mailpit");
        Directory.CreateDirectory(databaseDir);

        var databasePath = Path.Combine(databaseDir, "mailpit.db");
        var legacyDir = Path.Combine(_paths.DataRoot, "mailpit");
        var legacyPath = Path.Combine(legacyDir, "mailpit.db");
        if (!File.Exists(databasePath) && File.Exists(legacyPath))
        {
            File.Move(legacyPath, databasePath);
            if (Directory.Exists(legacyDir) && !Directory.EnumerateFileSystemEntries(legacyDir).Any())
            {
                Directory.Delete(legacyDir);
            }
        }

        return databasePath;
    }

    private static string? ResolveExecutable(string installPath)
    {
        var candidates = new[]
        {
            Path.Combine(installPath, "mailpit.exe"),
            Path.Combine(installPath, "bin", "mailpit.exe"),
            Path.Combine(installPath, "mailpit")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private void NotifyStatusChanged() => StatusChanged?.Invoke(this, EventArgs.Empty);
}

public sealed record MailpitStatus
{
    public bool Enabled { get; init; }
    public bool Installed { get; init; }
    public bool Running { get; init; }
    public ProcessStatus Status { get; init; } = ProcessStatus.Stopped;
    public int? Pid { get; init; }
    public string WebUrl { get; init; } = string.Empty;
    public string OpenLabel { get; init; } = string.Empty;
    public string SmtpEndpoint { get; init; } = string.Empty;
    public string? Message { get; init; }
}
