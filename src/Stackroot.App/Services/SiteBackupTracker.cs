using System.Collections.Concurrent;

namespace Stackroot.App.Services;

public enum SiteOperationType { Backup, Restore, Import }

public sealed record ActiveSiteOperation(string SiteId, string SiteDomain, SiteOperationType Type);

public sealed record CriticalOperationEventArgs(
    string SiteId,
    string SiteDomain,
    SiteOperationType OperationType);

public sealed class SiteBackupTracker
{
    private readonly ConcurrentDictionary<string, ActiveSiteOperation> _active =
        new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<string>? BackupStarted;
    public event EventHandler<string>? BackupEnded;

    /// <summary>
    /// Fires for any operation type (Backup, Restore, Import) when it begins.
    /// </summary>
    public event EventHandler<CriticalOperationEventArgs>? OperationStarted;

    /// <summary>
    /// Fires for any operation type (Backup, Restore, Import) when it ends.
    /// </summary>
    public event EventHandler<CriticalOperationEventArgs>? OperationEnded;

    public void Begin(string siteId, string siteDomain, SiteOperationType type = SiteOperationType.Backup)
    {
        _active.TryAdd(Key(siteId, type), new ActiveSiteOperation(siteId, siteDomain, type));
        var args = new CriticalOperationEventArgs(siteId, siteDomain, type);
        OperationStarted?.Invoke(this, args);
        if (type == SiteOperationType.Backup)
            BackupStarted?.Invoke(this, siteId);
    }

    public void End(string siteId, SiteOperationType type = SiteOperationType.Backup)
    {
        var domain = _active.TryGetValue(Key(siteId, type), out var op) ? op.SiteDomain : siteId;
        _active.TryRemove(Key(siteId, type), out _);
        var args = new CriticalOperationEventArgs(siteId, domain, type);
        OperationEnded?.Invoke(this, args);
        if (type == SiteOperationType.Backup)
            BackupEnded?.Invoke(this, siteId);
    }

    /// <summary>Checks if a site has an active operation of the specified type (defaults to Backup).</summary>
    public bool IsActive(string siteId, SiteOperationType type = SiteOperationType.Backup) =>
        _active.ContainsKey(Key(siteId, type));

    /// <summary>Checks if a site has any active operation (Backup, Restore, or Import).</summary>
    public bool IsActiveAny(string siteId) =>
        _active.Keys.Any(key => key.StartsWith(siteId + ":", StringComparison.OrdinalIgnoreCase));

    public bool HasActiveOperations => !_active.IsEmpty;

    public IReadOnlyList<ActiveSiteOperation> GetActiveOperations() =>
        _active.Values.OrderBy(op => op.SiteDomain).ThenBy(op => op.Type).ToList();

    private static string Key(string siteId, SiteOperationType type) => $"{siteId}:{type}";
}
