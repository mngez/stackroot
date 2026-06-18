using Stackroot.Core.Abstractions;
using Stackroot.Core.Settings;

namespace Stackroot.App.Helpers;

public static class SessionActivityMessages
{
    public static string SavingSettings(string name) => $"Saving {name} settings…";

    public static string Starting(string name) => $"Starting {name}…";

    public static string Installing(string label) => $"Installing {label}…";

    public static string PackageInstalled(string label) => $"{label} installed.";

    public static string PackageInstallFailed(string label) => $"{label} install failed.";

    public static string ProcessAction(string name, string verb, bool success, string? detail = null)
    {
        if (success)
        {
            return $"{name} {verb}.";
        }

        return string.IsNullOrWhiteSpace(detail)
            ? $"{name} {verb} failed."
            : $"{name} {verb} failed: {detail}";
    }

    public static string ServiceSettingsSaved(
        ServiceDefinition definition,
        string host,
        int port,
        bool sslEnabled,
        int? sslPort)
    {
        var parts = new List<string> { $"{definition.Name} settings saved" };
        if (definition.DefaultPort > 0)
        {
            parts.Add($"({host.Trim()}:{port})");
        }

        if (definition.Id == ServiceId.Nginx && definition.DefaultSslPort is > 0 && sslEnabled && sslPort is > 0)
        {
            parts.Add($"SSL :{sslPort.Value}");
        }

        return string.Join(" ", parts) + ".";
    }

    public static string ServiceEnabled(string name, bool enabled) =>
        enabled ? $"{name} enabled." : $"{name} disabled.";

    public static string ServiceAutoStart(string name, bool enabled) =>
        enabled ? $"{name} auto-start enabled." : $"{name} auto-start disabled.";

    public static string ServiceSupervise(string name, bool enabled) =>
        enabled ? $"{name} keep-alive enabled." : $"{name} keep-alive disabled.";

    public static string ServiceAction(string name, string verb, bool success, string? detail = null)
    {
        if (success)
        {
            return $"{name} {verb}.";
        }

        return string.IsNullOrWhiteSpace(detail)
            ? $"{name} {verb} failed."
            : $"{name} {verb} failed: {detail}";
    }

    public static string ServiceState(string name, string state) => $"{name}: {state}.";

    public static string PhpListenerStarted(string versionId, string host, int port) =>
        $"PHP {versionId} listener started on {host}:{port}.";

    public static string PhpListenersReady() => "PHP listeners ready.";

    public static string ProcessSaved(string name, bool created) =>
        created ? $"Process \"{name}\" added." : $"Process \"{name}\" updated.";

    public static string ProcessRemoved(string name) => $"Process \"{name}\" removed.";

    public static string ProcessBulkStarted(int count) =>
        count == 1 ? "Started 1 process (Start all)." : $"Started {count} processes (Start all).";

    public static string ProcessBulkStopped(int count) =>
        count == 1 ? "Stopped 1 process (Stop all)." : $"Stopped {count} processes (Stop all).";

    public static string ServiceBulkStarted(int count) =>
        count == 1 ? "Started 1 service (Start all)." : $"Started {count} services (Start all).";

    public static string ServiceBulkStopped(int count) =>
        count == 1 ? "Stopped 1 service (Stop all)." : $"Stopped {count} services (Stop all).";

    public static string DatabaseCreated(string name, SqlEngine engine) =>
        $"Database \"{name}\" created ({engine}).";

    public static string DatabaseDeleted(string name) => $"Database \"{name}\" deleted.";

    public static string DatabaseBackupCreated(string name, string path) =>
        $"Backup created for \"{name}\": {System.IO.Path.GetFileName(path)}.";

    public static string DatabaseBackupRestored(string name, string fileName) =>
        $"Restored \"{name}\" from {fileName}.";

    public static string SiteEnabled(string domain, bool enabled) =>
        enabled ? $"Site \"{domain}\" enabled." : $"Site \"{domain}\" disabled.";

    public static string SiteDeleted(string domain) => $"Site \"{domain}\" deleted.";

    public static string SiteCreated(string domain) => $"Site \"{domain}\" created.";

    public static string SiteUpdated(string domain) => $"Site \"{domain}\" updated.";

    public static string SiteQuickActionRunning(string label) => $"Running {label}…";

    public static string SiteQuickActionResult(string domain, string summary) => $"[{domain}] {summary}";

    public static string SiteProcessPresetAdded(string name) => $"Added process preset: {name}.";

    public static string SiteProcessEnabled(string name, bool enabled) =>
        enabled ? $"Process \"{name}\" enabled." : $"Process \"{name}\" disabled.";

    public static string SiteProcessBulkStarted(int count, string siteLabel) =>
        count == 1
            ? $"Started 1 process on {siteLabel}."
            : $"Started {count} processes on {siteLabel}.";

    public static string SiteProcessBulkStopped(int count, string siteLabel) =>
        count == 1
            ? $"Stopped 1 process on {siteLabel}."
            : $"Stopped {count} processes on {siteLabel}.";

    public static string GeneralSettingsSaved() => "General settings saved.";

    public static string SslCertificateTrust(bool success, string? detail = null)
    {
        if (success)
        {
            return string.IsNullOrWhiteSpace(detail) ? "SSL certificate trusted." : detail;
        }

        return string.IsNullOrWhiteSpace(detail) ? "Failed to trust SSL certificate." : detail;
    }

    public static string PhpVersionRestarted(string versionId) => $"PHP {versionId} restarted.";

    public static string NginxRebuilt(string message) => message;

    public static string PackageUninstalled(string label) => $"{label} uninstalled.";

    public static string NodeVersionInstalled(string version) => $"Node {version} installed and activated.";

    public static string NodeVersionUninstalled(string version) => $"Node {version} uninstalled.";

    public static string NodeVersionActivated(string version) => $"Node {version} set as active.";

    public static string PhpVersionActivated(string versionId) => $"PHP {versionId} set as active.";

    public static string PhpVersionUninstalled(string versionId) => $"PHP {versionId} uninstalled.";

    public static string FormatLiveState(ServiceDefinition definition, ServiceInfo info)
    {
        if (info.Enabled == false)
        {
            return "Disabled";
        }

        if (info.Installed == false && !string.IsNullOrWhiteSpace(definition.PackageId))
        {
            return "Not installed";
        }

        if (definition.Runtime == ServiceRuntime.Library)
        {
            return info.PortOpen == true ? "Ready" : "Unavailable";
        }

        return info.Status switch
        {
            ServiceStatus.Running => "Running",
            ServiceStatus.Starting => "Starting",
            ServiceStatus.Error => string.IsNullOrWhiteSpace(info.Message) ? "Error" : info.Message,
            _ => "Stopped"
        };
    }
}
