using System.Collections.Concurrent;
using System.Diagnostics;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Services.Lifecycle;
using Stackroot.Core.Windows;

namespace Stackroot.Core.Services;

public static class PhpCgiRuntime
{
    private sealed record Listener(string VersionId, int Port, int Pid);

    private static readonly ConcurrentDictionary<string, Listener> Managed = new(StringComparer.OrdinalIgnoreCase);
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
            return new ServiceLifecycleResult(true);
        }

        await EnsureSync.WaitAsync(cancellationToken);
        try
        {
            var staleKeys = Managed.Keys.Where(k => !versions.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
            foreach (var stale in staleKeys)
            {
                if (Managed.TryRemove(stale, out var staleListener))
                {
                    ProcessKiller.TryKill(staleListener.Pid);
                }
            }

            var failures = new List<string>();
            foreach (var versionId in versions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var verWatch = System.Diagnostics.Stopwatch.StartNew();
                diagnostics?.LogActivity("PHP", $"Starting FastCGI for {versionId}…");
                var index = PhpCgiPlanner.ResolveVersionIndex(registry, versionId);
                if (index is null)
                {
                    failures.Add($"PHP package not installed: {versionId}");
                    continue;
                }

            var port = PhpCgiPlanner.ResolvePlannedPort(settings, index.Value);

            int? staleTrackedPid = null;
            if (Managed.TryGetValue(versionId, out var existing))
            {
                staleTrackedPid = existing.Pid;
                if (!IsProcessAlive(existing.Pid))
                {
                    Managed.TryRemove(versionId, out _);
                    staleTrackedPid = existing.Pid;
                }
                else if (!forceRestart &&
                    existing.Port == port &&
                    IsManagedListenerHealthy(existing) &&
                    await PortProbe.IsPortOpenAsync(host, port))
                {
                    diagnostics?.LogActivity("PHP", $"FastCGI for {versionId} already healthy — skipping");
                    continue;
                }
                else
                {
                    diagnostics?.LogActivity("PHP", $"Killing stale listener for {versionId} (pid {existing.Pid})");
                    ProcessKiller.TryKill(existing.Pid);
                    Managed.TryRemove(versionId, out _);
                }
            }

            var package = registry.GetById(versionId);
            if (package is null)
            {
                failures.Add($"PHP package not installed: {versionId}");
                continue;
            }

            if (!await ReclaimPortAsync(host, port, package.InstallPath, staleTrackedPid, cancellationToken, diagnostics, versionId))
            {
                failures.Add($"Could not free FastCGI port {host}:{port} for {versionId}. Close the process using this port and try again.");
                continue;
            }
            ProcessPortTools.InvalidatePortCache(port);

            var phpCgiPath = ResolvePhpCgiPath(package.InstallPath);
            if (phpCgiPath is null)
            {
                failures.Add($"php-cgi.exe not found for {versionId}");
                continue;
            }

            Process? process = null;
            string? lastAttemptFailure = null;
            var started = false;
            for (var attempt = 0; attempt < 5 && !started; attempt++)
            {
                if (attempt > 0)
                {
                    await ReclaimPortAsync(host, port, package.InstallPath, staleTrackedPid, cancellationToken);
                    ProcessPortTools.InvalidatePortCache(port);
                }

                try
                {
                    var iniPath = PhpConfigPaths.ResolveExistingDefaultIniPath(paths.ConfigRoot, versionId);
                    if (string.IsNullOrWhiteSpace(iniPath))
                    {
                        failures.Add($"php.ini not found for {versionId}. Open Stackroot once or save PHP settings.");
                        continue;
                    }

                    if (attempt == 0)
                        diagnostics?.LogActivity("PHP", $"Spawning php-cgi for {versionId}…");
                    else
                        diagnostics?.LogActivity("PHP", $"Retrying php-cgi for {versionId} (attempt {attempt + 1}/5)…");
                    process = ServiceProcessTools.StartProcess(
                        phpCgiPath,
                        ["-b", $"{host}:{port}", "-c", iniPath],
                        Path.GetDirectoryName(phpCgiPath) ?? package.InstallPath,
                        jobManager);
                }
                catch (Exception ex)
                {
                    failures.Add($"Failed to start php-cgi for {versionId}: {ex.Message}");
                    break;
                }

                var listening = await PortProbe.WaitForPortAsync(host, port, attempts: 25, delayMs: 200, cancellationToken);
                var owned = listening && await WaitForPhpCgiOwnershipAsync(process.Id, host, port, cancellationToken).ConfigureAwait(false);
                if (!listening || !owned)
                {
                    ProcessKiller.TryKill(process.Id);
                    WaitForExit(process, 1000);
                    lastAttemptFailure = DescribePhpCgiAttemptFailure(process, host, port, listening);
                    process.Dispose();
                    process = null;
                    ProcessPortTools.InvalidatePortCache(port);
                    continue;
                }

                started = true;
                diagnostics?.LogActivity("PHP", $"FastCGI listener for {versionId} bound to {host}:{port} in {verWatch.ElapsedMilliseconds}ms");
            }

            if (!started || process is null)
            {
                diagnostics?.LogActivity("PHP", $"FastCGI listener for {versionId} FAILED after {verWatch.ElapsedMilliseconds}ms");
                failures.Add(
                    $"php-cgi did not bind FastCGI on {host}:{port} for {versionId} after automatic recovery attempts."
                    + (string.IsNullOrWhiteSpace(lastAttemptFailure) ? string.Empty : $" {lastAttemptFailure}"));
                continue;
            }

            Managed[versionId] = new Listener(versionId, port, process.Id);
        }

            if (failures.Count > 0)
            {
                return new ServiceLifecycleResult(false, string.Join(Environment.NewLine, failures));
            }

            return new ServiceLifecycleResult(true);
        }
        finally
        {
            EnsureSync.Release();
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
        foreach (var entry in Managed.Values)
        {
            ProcessKiller.TryKill(entry.Pid);
            ProcessPortTools.InvalidatePortCache(entry.Port);
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

        if (Managed.TryRemove(versionId, out var listener))
        {
            ProcessKiller.TryKill(listener.Pid);
            ProcessPortTools.InvalidatePortCache(listener.Port);
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

        var port = PhpCgiPlanner.ResolvePlannedPortForVersion(settings, registry, versionId);
        var package = registry.GetById(versionId);
        if (port is not int resolvedPort || package is null)
        {
            return;
        }

        StackrootManagedProcessResolver.TryKillPids(
            StackrootManagedProcessResolver.ResolveOwnedPhpCgiPidsOnPort(
                resolvedPort,
                package.InstallPath,
                staleTrackedPid));
        ProcessPortTools.InvalidatePortCache(resolvedPort);
    }

    public static IReadOnlyDictionary<string, int> ActiveListeners()
    {
        return Managed.Values.ToDictionary(v => v.VersionId, v => v.Port, StringComparer.OrdinalIgnoreCase);
    }

    public static bool TryGetManagedListenerPid(string versionId, out int pid)
    {
        if (Managed.TryGetValue(versionId, out var listener) && IsProcessAlive(listener.Pid))
        {
            pid = listener.Pid;
            return true;
        }

        pid = 0;
        return false;
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

    private static bool IsManagedListenerHealthy(Listener listener)
    {
        if (!IsProcessAlive(listener.Pid))
        {
            return false;
        }

        var portOwners = ServiceProcessTools.FindPidsListeningOnPort(listener.Port);
        if (portOwners.Count == 0)
        {
            // Port scan returned nothing — this could be a netstat failure.
            // The process is alive, so assume it is still bound to the port.
            return true;
        }

        return portOwners.Count == 1 &&
               portOwners[0] == listener.Pid &&
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
        var stderr = process.HasExited ? ReadStreamTail(process.StandardError) : string.Empty;
        var stdout = process.HasExited ? ReadStreamTail(process.StandardOutput) : string.Empty;
        var output = string.IsNullOrWhiteSpace(stderr) && string.IsNullOrWhiteSpace(stdout)
            ? string.Empty
            : $" Output: {string.Join(" | ", new[] { stderr, stdout }.Where(text => !string.IsNullOrWhiteSpace(text)))}";

        return $"Last attempt: {state}; port {host}:{port} listening={listening}; {ownerText}.{output}";
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

    private static string ReadStreamTail(StreamReader reader)
    {
        try
        {
            var text = reader.ReadToEnd().Trim();
            return text.Length <= 500 ? text : text[^500..];
        }
        catch
        {
            return string.Empty;
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
