using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Nginx;
using Stackroot.Core.Services.Lifecycle;
using Stackroot.Core.Windows;

namespace Stackroot.Core.Services;

public static class PhpCgiRuntime
{
    /// <summary>
    /// php-cgi recycles itself after this many requests to bound memory growth.
    /// With a worker pool in front, a recycling worker causes no downtime — nginx
    /// load-balances to the siblings and the exited worker is respawned immediately.
    /// </summary>
    private const string FcgiMaxRequests = "10000";

    private sealed class Worker
    {
        public Worker(int port, Process process)
        {
            Port = port;
            Process = process;
            Pid = process.Id;
        }

        public int Port { get; }
        public Process Process { get; }
        public int Pid { get; }

        /// <summary>Set when the worker is being torn down on purpose so its exit does not trigger a respawn.</summary>
        public bool Stopping { get; set; }
    }

    /// <summary>The captured parameters needed to reconcile a version's pool, including after a worker exits unexpectedly.</summary>
    private sealed record PoolContext(
        StackrootPaths Paths,
        InstallRegistryStore Registry,
        AppSettings Settings,
        IProcessJobManager JobManager,
        string Host,
        IDiagnosticsReporter? Diagnostics);

    private sealed class Pool
    {
        public required string VersionId { get; init; }
        public int AnchorPort { get; set; }
        public List<Worker> Workers { get; } = [];
        public PoolContext? Context { get; set; }
    }

    private static readonly ConcurrentDictionary<string, Pool> Managed = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim EnsureSync = new(1, 1);

    public static async Task<ServiceLifecycleResult> EnsurePhpFastCgiAsync(
        StackrootPaths paths,
        InstallRegistryStore registry,
        AppSettings settings,
        IProcessJobManager jobManager,
        IReadOnlyList<string>? versionIds = null,
        CancellationToken cancellationToken = default,
        bool forceRestart = false,
        IDiagnosticsReporter? diagnostics = null)
    {
        var host = string.IsNullOrWhiteSpace(settings.Php.FpmHost) ? "127.0.0.1" : settings.Php.FpmHost;
        var versions = versionIds?.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];

        if (versions.Count == 0)
        {
            // Keep the upstream block in sync with installed versions even when nothing
            // is required yet, so any vhost referencing a version resolves to a real upstream.
            WriteUpstreamsConf(paths, settings, registry);
            return new ServiceLifecycleResult(true);
        }

        var context = new PoolContext(paths, registry, settings, jobManager, host, diagnostics);

        await EnsureSync.WaitAsync(cancellationToken);
        try
        {
            var staleKeys = Managed.Keys.Where(k => !versions.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
            foreach (var stale in staleKeys)
            {
                if (Managed.TryRemove(stale, out var stalePool))
                {
                    KillPool(stalePool);
                }
            }

            var failures = new List<string>();
            foreach (var versionId in versions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var failure = await ReconcileVersionLockedAsync(context, versionId, forceRestart, cancellationToken).ConfigureAwait(false);
                if (failure is not null)
                {
                    failures.Add(failure);
                }
            }

            // Topology (which ports back which version) only depends on settings + registry,
            // so refresh the upstream block once per batch regardless of worker liveness.
            WriteUpstreamsConf(paths, settings, registry);

            return failures.Count > 0
                ? new ServiceLifecycleResult(false, string.Join(Environment.NewLine, failures))
                : new ServiceLifecycleResult(true);
        }
        finally
        {
            EnsureSync.Release();
        }
    }

    /// <summary>
    /// Brings a single version's pool to the desired worker count. Healthy workers are kept;
    /// missing or dead ones are (re)started; surplus workers (after a pool-size shrink) are killed.
    /// Caller must hold <see cref="EnsureSync"/>.
    /// </summary>
    private static async Task<string?> ReconcileVersionLockedAsync(
        PoolContext context,
        string versionId,
        bool forceRestart,
        CancellationToken cancellationToken)
    {
        var verWatch = Stopwatch.StartNew();
        var diagnostics = context.Diagnostics;
        var registry = context.Registry;
        var settings = context.Settings;
        var host = context.Host;

        var index = PhpCgiPlanner.ResolveVersionIndex(registry, versionId);
        if (index is null)
        {
            return $"PHP package not installed: {versionId}";
        }

        var package = registry.GetById(versionId);
        if (package is null)
        {
            return $"PHP package not installed: {versionId}";
        }

        var phpCgiPath = ResolvePhpCgiPath(package.InstallPath);
        if (phpCgiPath is null)
        {
            return $"php-cgi.exe not found for {versionId}";
        }

        var iniPath = PhpConfigPaths.ResolveExistingDefaultIniPath(context.Paths.ConfigRoot, versionId);
        if (string.IsNullOrWhiteSpace(iniPath))
        {
            return $"php.ini not found for {versionId}. Open Stackroot once or save PHP settings.";
        }

        var desiredPorts = PhpCgiPlanner.ResolveWorkerPorts(settings, index.Value);
        var pool = Managed.GetOrAdd(versionId, static id => new Pool { VersionId = id });
        pool.Context = context;
        pool.AnchorPort = desiredPorts.Count > 0 ? desiredPorts[0] : PhpCgiPlanner.ResolvePlannedPort(settings, index.Value);

        // Drop workers that are no longer wanted (port outside the desired block) or that died.
        for (var i = pool.Workers.Count - 1; i >= 0; i--)
        {
            var worker = pool.Workers[i];
            var portWanted = desiredPorts.Contains(worker.Port);
            if (forceRestart || !portWanted || !IsProcessAlive(worker.Pid))
            {
                worker.Stopping = true;
                ProcessKiller.TryKill(worker.Pid);
                ProcessPortTools.InvalidatePortCache(worker.Port);
                pool.Workers.RemoveAt(i);
            }
        }

        var failures = new List<string>();
        var startedAny = false;
        foreach (var port in desiredPorts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var existing = pool.Workers.FirstOrDefault(w => w.Port == port);
            if (existing is not null
                && !forceRestart
                && IsManagedWorkerHealthy(existing)
                && await PortProbe.IsPortOpenAsync(host, port).ConfigureAwait(false))
            {
                continue;
            }

            if (existing is not null)
            {
                existing.Stopping = true;
                ProcessKiller.TryKill(existing.Pid);
                pool.Workers.Remove(existing);
            }

            var (worker, failure) = await SpawnWorkerLockedAsync(
                context, versionId, package, phpCgiPath, iniPath, port, index.Value, cancellationToken).ConfigureAwait(false);
            if (worker is null)
            {
                failures.Add(failure ?? $"php-cgi did not bind {host}:{port} for {versionId}.");
                continue;
            }

            pool.Workers.Add(worker);
            startedAny = true;
        }

        if (startedAny)
        {
            diagnostics?.LogActivity(
                "PHP",
                $"FastCGI pool for {versionId}: {pool.Workers.Count}/{desiredPorts.Count} worker(s) up in {verWatch.ElapsedMilliseconds}ms");
        }

        if (pool.Workers.Count == 0)
        {
            Managed.TryRemove(versionId, out _);
            return failures.Count > 0
                ? string.Join(" ", failures)
                : $"php-cgi did not start any FastCGI worker for {versionId}.";
        }

        // Partial pool is still serving (nginx fails over), so do not surface a hard error;
        // the event-driven respawn and the recovery timer will fill the gaps.
        return null;
    }

    private static async Task<(Worker? Worker, string? Failure)> SpawnWorkerLockedAsync(
        PoolContext context,
        string versionId,
        InstalledPackage package,
        string phpCgiPath,
        string iniPath,
        int port,
        int versionIndex,
        CancellationToken cancellationToken)
    {
        var host = context.Host;
        var diagnostics = context.Diagnostics;
        var workerIndex = port - PhpCgiPlanner.ResolvePlannedPort(context.Settings, versionIndex);
        var stderrLogPath = PhpLogPaths.GetCgiWorkerStderrLogPath(context.Paths.LogsRoot, versionId, workerIndex);
        var environment = new Dictionary<string, string?> { ["PHP_FCGI_MAX_REQUESTS"] = FcgiMaxRequests };

        string? lastAttemptFailure = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!await ReclaimPortAsync(host, port, package.InstallPath, null, cancellationToken, diagnostics, versionId).ConfigureAwait(false))
            {
                lastAttemptFailure = $"Could not free FastCGI port {host}:{port}.";
                continue;
            }

            ProcessPortTools.InvalidatePortCache(port);

            Process process;
            try
            {
                diagnostics?.LogActivity(
                    "PHP",
                    attempt == 0
                        ? $"Spawning php-cgi for {versionId} on {host}:{port} (worker {workerIndex})…"
                        : $"Retrying php-cgi for {versionId} on {host}:{port} (attempt {attempt + 1}/5)…");
                process = ServiceProcessTools.StartProcess(
                    phpCgiPath,
                    ["-b", $"{host}:{port}", "-c", iniPath],
                    Path.GetDirectoryName(phpCgiPath) ?? package.InstallPath,
                    context.JobManager,
                    environment: environment,
                    stderrLogPath: stderrLogPath);
            }
            catch (Exception ex)
            {
                return (null, $"Failed to start php-cgi for {versionId}: {ex.Message}");
            }

            var listening = await PortProbe.WaitForPortAsync(host, port, attempts: 25, delayMs: 200, cancellationToken).ConfigureAwait(false);
            var owned = listening && await WaitForPhpCgiOwnershipAsync(process.Id, host, port, cancellationToken).ConfigureAwait(false);
            if (!listening || !owned)
            {
                ProcessKiller.TryKill(process.Id);
                WaitForExit(process, 1000);
                lastAttemptFailure = DescribePhpCgiAttemptFailure(process, host, port, listening);
                process.Dispose();
                ProcessPortTools.InvalidatePortCache(port);
                continue;
            }

            var worker = new Worker(port, process);
            WireWorkerExit(versionId, worker);
            return (worker, null);
        }

        return (null,
            $"php-cgi did not bind FastCGI on {host}:{port} for {versionId} after automatic recovery attempts."
            + (string.IsNullOrWhiteSpace(lastAttemptFailure) ? string.Empty : $" {lastAttemptFailure}"));
    }

    /// <summary>
    /// Reacts to a worker exiting (php-cgi recycle after PHP_FCGI_MAX_REQUESTS, or a crash) by
    /// immediately reconciling that version's pool — no waiting for the recovery poll timer.
    /// </summary>
    private static void WireWorkerExit(string versionId, Worker worker)
    {
        worker.Process.EnableRaisingEvents = true;
        worker.Process.Exited += (_, _) =>
        {
            if (worker.Stopping || ApplicationShutdownState.IsClosing)
            {
                return;
            }

            _ = RespawnAfterExitAsync(versionId);
        };
    }

    private static async Task RespawnAfterExitAsync(string versionId)
    {
        try
        {
            await EnsureSync.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ApplicationShutdownState.IsClosing)
                {
                    return;
                }

                if (!Managed.TryGetValue(versionId, out var pool) || pool.Context is null)
                {
                    return;
                }

                pool.Context.Diagnostics?.LogActivity(
                    "PHP",
                    $"php-cgi worker for {versionId} exited — respawning immediately…");
                await ReconcileVersionLockedAsync(pool.Context, versionId, forceRestart: false, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                EnsureSync.Release();
            }
        }
        catch
        {
            // Best-effort fast restart; the recovery timer remains the backstop.
        }
    }

    public static Task<ServiceLifecycleResult> EnsureStackPhpCgiAsync(
        StackrootPaths paths,
        InstallRegistryStore registry,
        AppSettings settings,
        IProcessJobManager jobManager,
        IEnumerable<string>? extraVersionIds = null,
        PackageCatalogStore? catalog = null,
        CancellationToken cancellationToken = default)
    {
        var versionIds = PhpCgiPlanner.ResolveRequiredVersionIds(settings, registry, extraVersionIds, catalog);
        return EnsurePhpFastCgiAsync(
            paths,
            registry,
            settings,
            jobManager,
            versionIds,
            cancellationToken);
    }

    public static IReadOnlyList<string> ResolveRequiredVersionIds(
        AppSettings settings,
        InstallRegistryStore registry,
        IEnumerable<string>? extraVersionIds = null,
        PackageCatalogStore? catalog = null) =>
        PhpCgiPlanner.ResolveRequiredVersionIds(settings, registry, extraVersionIds, catalog);

    public static int? ResolvePlannedPortForVersion(
        AppSettings settings,
        InstallRegistryStore registry,
        string versionId) =>
        PhpCgiPlanner.ResolvePlannedPortForVersion(settings, registry, versionId);

    public static Task StopAllPhpCgiAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var pool in Managed.Values)
        {
            KillPool(pool);
        }

        Managed.Clear();
        return Task.CompletedTask;
    }

    public static Task StopPhpCgiAsync(string versionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(versionId))
        {
            return Task.CompletedTask;
        }

        if (Managed.TryRemove(versionId, out var pool))
        {
            KillPool(pool);
        }

        return Task.CompletedTask;
    }

    public static void KillOwnedPhpCgiOnPort(
        AppSettings settings,
        InstallRegistryStore registry,
        string versionId,
        int? staleTrackedPid = null)
    {
        if (string.IsNullOrWhiteSpace(versionId))
        {
            return;
        }

        var package = registry.GetById(versionId);
        if (package is null)
        {
            return;
        }

        foreach (var port in PhpCgiPlanner.ResolveWorkerPortsForVersion(settings, registry, versionId))
        {
            StackrootManagedProcessResolver.TryKillPids(
                StackrootManagedProcessResolver.ResolveOwnedPhpCgiPidsOnPort(
                    port,
                    package.InstallPath,
                    staleTrackedPid));
            ProcessPortTools.InvalidatePortCache(port);
        }
    }

    /// <summary>versionId → anchor (worker 0) port, for every version with at least one live worker.</summary>
    public static IReadOnlyDictionary<string, int> ActiveListeners()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pool in Managed.Values)
        {
            if (pool.Workers.Any(w => IsProcessAlive(w.Pid)))
            {
                result[pool.VersionId] = pool.AnchorPort;
            }
        }

        return result;
    }

    public static bool TryGetManagedListenerPid(string versionId, out int pid)
    {
        if (Managed.TryGetValue(versionId, out var pool))
        {
            var alive = pool.Workers.FirstOrDefault(w => IsProcessAlive(w.Pid));
            if (alive is not null)
            {
                pid = alive.Pid;
                return true;
            }

            if (pool.Workers.Count == 0)
            {
                Managed.TryRemove(versionId, out _);
            }
        }

        pid = 0;
        return false;
    }

    /// <summary>All currently-alive worker PIDs for a version (used for memory accounting).</summary>
    public static IReadOnlyList<int> GetManagedWorkerPids(string versionId)
    {
        if (!Managed.TryGetValue(versionId, out var pool))
        {
            return [];
        }

        return pool.Workers.Where(w => IsProcessAlive(w.Pid)).Select(w => w.Pid).ToList();
    }

    private static void KillPool(Pool pool)
    {
        foreach (var worker in pool.Workers)
        {
            worker.Stopping = true;
            ProcessKiller.TryKill(worker.Pid);
            ProcessPortTools.InvalidatePortCache(worker.Port);
        }

        pool.Workers.Clear();
    }

    /// <summary>
    /// Writes <c>conf/php-upstreams.conf</c> with one upstream per installed PHP version,
    /// listing every worker port. Written for all installed versions (not just running pools)
    /// so any site referencing a version resolves to a valid upstream even mid-restart.
    /// </summary>
    private static void WriteUpstreamsConf(StackrootPaths paths, AppSettings settings, InstallRegistryStore registry)
    {
        try
        {
            var host = string.IsNullOrWhiteSpace(settings.Php.FpmHost) ? "127.0.0.1" : settings.Php.FpmHost.Trim();
            var confDir = Path.Combine(NginxRuntime.nginxPrefix(paths), "conf");
            Directory.CreateDirectory(confDir);

            var ordered = PhpCgiPlanner.OrderInstalledVersionIds(registry);
            var sb = new StringBuilder();
            sb.AppendLine("# Generated by Stackroot — php-cgi worker pools. Do not edit.");
            for (var index = 0; index < ordered.Count; index++)
            {
                var versionId = ordered[index];
                var ports = PhpCgiPlanner.ResolveWorkerPorts(settings, index);
                if (ports.Count == 0)
                {
                    continue;
                }

                sb.AppendLine($"upstream {PhpFastCgiNaming.UpstreamName(versionId)} {{");
                foreach (var port in ports)
                {
                    // fail_timeout keeps a recycling worker out of rotation only briefly.
                    sb.AppendLine($"    server {host}:{port} fail_timeout=5s;");
                }

                sb.AppendLine("}");
            }

            var target = Path.Combine(confDir, "php-upstreams.conf");
            File.WriteAllText(target, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            // Best-effort; nginx keeps any previously written upstream file.
        }
    }

    private static async Task<bool> ReclaimPortAsync(
        string host,
        int port,
        string phpInstallPath,
        int? staleTrackedPid,
        CancellationToken cancellationToken,
        IDiagnosticsReporter? diagnostics = null,
        string? versionId = null)
    {
        // Fast path: if the port is already closed, skip the expensive netstat scan.
        if (!await PortProbe.IsPortOpenAsync(host, port))
        {
            return true;
        }

        diagnostics?.LogActivity("PHP", $"Reclaiming port {host}:{port} for {versionId}…");

        StackrootManagedProcessResolver.TryKillPids(
            StackrootManagedProcessResolver.ResolveOwnedPhpCgiPidsOnPort(port, phpInstallPath, staleTrackedPid));
        ProcessPortTools.InvalidatePortCache(port);

        if (await PortProbe.WaitForPortClosedAsync(host, port, cancellationToken: cancellationToken))
        {
            return true;
        }

        StackrootManagedProcessResolver.TryKillPids(
            StackrootManagedProcessResolver.ResolvePhpCgiPidsOnPort(port));
        ProcessPortTools.InvalidatePortCache(port);

        return await PortProbe.WaitForPortClosedAsync(host, port, attempts: 25, cancellationToken: cancellationToken);
    }

    private static bool IsManagedWorkerHealthy(Worker worker)
    {
        if (!IsProcessAlive(worker.Pid))
        {
            return false;
        }

        var portOwners = ServiceProcessTools.FindPidsListeningOnPort(worker.Port);
        if (portOwners.Count == 0)
        {
            // Port scan returned nothing — this could be a netstat failure.
            // The process is alive, so assume it is still bound to the port.
            return true;
        }

        return portOwners.Count == 1 &&
               portOwners[0] == worker.Pid &&
               IsPhpCgiProcess(portOwners[0]);
    }

    private static bool IsPortOwnedByPhpCgi(int expectedPid, int port)
    {
        if (!IsPhpCgiProcess(expectedPid))
        {
            return false;
        }

        ProcessPortTools.InvalidatePortCache(port);
        var portOwners = ServiceProcessTools.FindPidsListeningOnPort(port);
        return portOwners.Contains(expectedPid);
    }

    private static async Task<bool> WaitForPhpCgiOwnershipAsync(
        int expectedPid,
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsProcessAlive(expectedPid))
            {
                return false;
            }

            if (IsPortOwnedByPhpCgi(expectedPid, port))
            {
                return true;
            }

            var owners = ServiceProcessTools.FindPidsListeningOnPort(port);
            if (owners.Count > 0 && !owners.Contains(expectedPid))
            {
                return false;
            }

            if (await PortProbe.IsPortOpenAsync(host, port).ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static string DescribePhpCgiAttemptFailure(Process process, string host, int port, bool listening)
    {
        var owners = ServiceProcessTools.FindPidsListeningOnPort(port);
        var state = process.HasExited
            ? $"process exited with code {SafeExitCode(process)}"
            : "process was still running";
        var ownerText = owners.Count == 0 ? "no listener owner was reported" : $"listener owner pid(s): {string.Join(", ", owners)}";

        return $"Last attempt: {state}; port {host}:{port} listening={listening}; {ownerText}.";
    }

    private static string SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode.ToString();
        }
        catch
        {
            return "unknown";
        }
    }

    private static void WaitForExit(Process process, int milliseconds)
    {
        try
        {
            process.WaitForExit(milliseconds);
        }
        catch
        {
            // Best effort only.
        }
    }

    private static bool IsPhpCgiProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName.Contains("php-cgi", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolvePhpCgiPath(string installPath)
    {
        var candidates = new[]
        {
            Path.Combine(installPath, "php-cgi.exe"),
            Path.Combine(installPath, "bin", "php-cgi.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
