using System.Windows.Input;
using Stackroot.App.Commands;
using Stackroot.App.Services;

namespace Stackroot.App.ViewModels;

public sealed class ImportSiteDialogViewModel : ViewModelBase
{
    private readonly HashSet<string> _existingDomains;
    private bool _restoreFiles;
    private bool _restoreDatabases;
    private bool _restoreProcesses;
    private bool _restoreScheduledTasks;

    public ImportSiteDialogViewModel(
        BackupManifest manifest,
        SiteImportConflict conflict,
        IEnumerable<string> allExistingDomains)
    {
        // Include both primary domains and aliases from all existing sites in conflict-check set
        _existingDomains = new HashSet<string>(allExistingDomains, StringComparer.OrdinalIgnoreCase);

        SiteName = manifest.SiteName;
        OriginalDomain = manifest.SiteDomain;
        BackupDate = FormatDate(manifest.BackupDate);
        HasFiles = manifest.Contents.HasFiles;
        HasDatabases = manifest.Contents.HasDatabases;
        HasProcesses = manifest.Contents.HasProcesses;
        HasScheduledTasks = manifest.Contents.HasScheduledTasks;

        AllAliases = conflict.AllAliases;
        ConflictingAliases = conflict.ConflictingAliases;
        ConflictingDatabases = conflict.ConflictingDatabaseNames;

        _restoreFiles = HasFiles;
        _restoreDatabases = HasDatabases;
        _restoreProcesses = HasProcesses;
        _restoreScheduledTasks = HasScheduledTasks;

        ImportCommand = new RelayCommand(
            _ => RequestClose?.Invoke(this, true),
            _ => !string.IsNullOrWhiteSpace(NewDomain) && !HasBlockingConflicts);
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(this, false));
    }

    public event EventHandler<bool>? RequestClose;

    public string SiteName { get; }
    public string OriginalDomain { get; }
    public string BackupDate { get; }
    public bool HasFiles { get; }
    public bool HasDatabases { get; }
    public bool HasProcesses { get; }
    public bool HasScheduledTasks { get; }

    // Additional domains from backup (informational)
    public IReadOnlyList<string> AllAliases { get; }
    public bool HasAliases => AllAliases.Count > 0;
    public string AllAliasesText => string.Join(", ", AllAliases);

    // Blocking conflicts
    public IReadOnlyList<string> ConflictingAliases { get; }
    public bool HasAliasConflicts => ConflictingAliases.Count > 0;
    public string ConflictingAliasesText => string.Join(", ", ConflictingAliases);

    public IReadOnlyList<string> ConflictingDatabases { get; }
    public bool HasDatabaseConflicts => ConflictingDatabases.Count > 0;
    public string ConflictingDatabasesText => string.Join(", ", ConflictingDatabases);

    public string NewDomain => OriginalDomain;

    public bool DomainConflict =>
        !string.IsNullOrWhiteSpace(OriginalDomain) &&
        _existingDomains.Contains(OriginalDomain.Trim());

    public bool HasBlockingConflicts => DomainConflict || HasDatabaseConflicts || HasAliasConflicts;

    public bool RestoreFiles
    {
        get => _restoreFiles;
        set => SetProperty(ref _restoreFiles, value);
    }

    public bool RestoreDatabases
    {
        get => _restoreDatabases;
        set => SetProperty(ref _restoreDatabases, value);
    }

    public bool RestoreProcesses
    {
        get => _restoreProcesses;
        set => SetProperty(ref _restoreProcesses, value);
    }

    public bool RestoreScheduledTasks
    {
        get => _restoreScheduledTasks;
        set => SetProperty(ref _restoreScheduledTasks, value);
    }

    public ICommand ImportCommand { get; }
    public ICommand CancelCommand { get; }

    public SiteRestoreOptions BuildOptions() => new(
        RestoreFiles: RestoreFiles && HasFiles,
        RestoreDatabases: RestoreDatabases && HasDatabases,
        RestoreProcesses: RestoreProcesses && HasProcesses,
        RestoreScheduledTasks: RestoreScheduledTasks && HasScheduledTasks);

    private static string FormatDate(string iso)
    {
        if (DateTimeOffset.TryParse(iso, out var dt))
            return dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        return iso;
    }
}
