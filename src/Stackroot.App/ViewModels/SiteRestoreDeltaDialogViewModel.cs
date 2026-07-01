using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Stackroot.App.Commands;
using Stackroot.App.Services;
using SiteModel = Stackroot.Core.Sites.Models.Site;

namespace Stackroot.App.ViewModels;

public sealed class RollbackDeltaItemViewModel : ViewModelBase
{
    private bool _willDelete;
    private bool _isIncluded = true;

    public RollbackDeltaItemViewModel(string id, string displayName, DeltaItemAction action, bool supportsIncludeToggle = false)
    {
        Id = id;
        DisplayName = displayName;
        Action = action;
        IsDelete = action == DeltaItemAction.Delete;
        _willDelete = IsDelete;
        CanToggleInclude = supportsIncludeToggle && !IsDelete;

        ActionBadge = action switch
        {
            DeltaItemAction.Replace => "↺",
            DeltaItemAction.Restore => "+",
            DeltaItemAction.Delete => "–",
            _ => "?"
        };

        var color = action switch
        {
            DeltaItemAction.Replace => Color.FromRgb(0x91, 0xA0, 0xB5),
            DeltaItemAction.Restore => Color.FromRgb(0x8F, 0xD6, 0xB6),
            DeltaItemAction.Delete => Color.FromRgb(0xE0, 0x52, 0x6B),
            _ => Color.FromRgb(0x91, 0xA0, 0xB5)
        };
        ActionBrush = new SolidColorBrush(color);
        ActionBrush.Freeze();

        ToggleKeepCommand = new RelayCommand(_ => WillDelete = !WillDelete);
        ToggleIncludeCommand = new RelayCommand(_ => IsIncluded = !IsIncluded, _ => CanToggleInclude);
    }

    public string Id { get; }
    public string DisplayName { get; }
    public DeltaItemAction Action { get; }
    public bool IsDelete { get; }
    public bool CanToggleInclude { get; }
    public string ActionBadge { get; }
    public SolidColorBrush ActionBrush { get; }
    public ICommand ToggleKeepCommand { get; }
    public ICommand ToggleIncludeCommand { get; }

    public bool WillDelete
    {
        get => _willDelete;
        set
        {
            if (SetProperty(ref _willDelete, value))
            {
                RaisePropertyChanged(nameof(WillKeep));
                RaisePropertyChanged(nameof(WillDeleteFinal));
                RaisePropertyChanged(nameof(WillKeepFinal));
            }
        }
    }

    public bool IsIncluded
    {
        get => _isIncluded;
        set => SetProperty(ref _isIncluded, value);
    }

    public bool WillKeep => !WillDelete;
    public bool WillDeleteFinal => IsDelete && WillDelete;
    public bool WillKeepFinal => IsDelete && WillKeep;
}

public sealed class SiteRestoreDeltaDialogViewModel : ViewModelBase
{
    private bool _restoreFiles;
    private bool _restoreDatabases;
    private bool _restoreProcesses;
    private bool _restoreScheduledTasks;
    private bool _skipFileSafetyCopy;
    private bool _skipDbSafetyCopy;
    private bool _isLoading = true;
    private string? _loadError;

    private long _extractedBytes;
    private long _dbRollbackBytes;

    public SiteRestoreDeltaDialogViewModel(SiteModel site, BackupManifest manifest)
    {
        SiteDomain = site.Domain;
        SiteName = manifest.SiteName;
        BackupDate = FormatDate(manifest.BackupDate);

        RestoreCommand = new RelayCommand(_ => RequestClose?.Invoke(this, true), _ => !IsLoading && !HasLoadError);
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(this, false));
    }

    public event EventHandler<bool>? RequestClose;

    public string SiteDomain { get; }
    public string SiteName { get; }
    public string BackupDate { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RaisePropertyChanged(nameof(IsLoaded));
                RaisePropertyChanged(nameof(ShowNothingToReport));
            }
        }
    }

    public bool IsLoaded => !_isLoading;

    public string? LoadError
    {
        get => _loadError;
        private set
        {
            if (SetProperty(ref _loadError, value))
                RaisePropertyChanged(nameof(HasLoadError));
        }
    }

    public bool HasLoadError => _loadError != null;

    public bool HasBackupFiles { get; private set; }
    public bool HasBackupDatabases { get; private set; }
    public bool HasBackupProcesses { get; private set; }
    public bool HasBackupScheduledTasks { get; private set; }

    public ObservableCollection<RollbackDeltaItemViewModel> DatabaseItems { get; } = [];
    public ObservableCollection<RollbackDeltaItemViewModel> ProcessItems { get; } = [];
    public ObservableCollection<RollbackDeltaItemViewModel> TaskItems { get; } = [];

    public bool HasDatabaseItems => DatabaseItems.Count > 0;
    public bool HasProcessItems => ProcessItems.Count > 0;
    public bool HasTaskItems => TaskItems.Count > 0;
    public bool HasNoDatabaseItems => !HasDatabaseItems;
    public bool HasNoProcessItems => !HasProcessItems;
    public bool HasNoTaskItems => !HasTaskItems;

    public bool ShowDatabasesSection => HasBackupDatabases || DatabaseItems.Any(i => i.IsDelete);
    public bool ShowProcessesSection => HasBackupProcesses || ProcessItems.Any(i => i.IsDelete);
    public bool ShowTasksSection => HasBackupScheduledTasks || TaskItems.Any(i => i.IsDelete);

    public bool HasSpaceEstimate => _extractedBytes > 0;
    public bool HasDbRollback => !_skipDbSafetyCopy && _dbRollbackBytes > 0;

    public string SpaceExtractionText
    {
        get
        {
            if (_extractedBytes <= 0) return string.Empty;
            var mb = Math.Max(1, (_extractedBytes + 1024L * 1024 - 1) / (1024 * 1024));
            return $"~{mb:N0} MB";
        }
    }

    public string SpaceRollbackText
    {
        get
        {
            if (!HasDbRollback) return string.Empty;
            var mb = (_dbRollbackBytes + 1024L * 1024 - 1) / (1024 * 1024);
            return $"~{mb:N0} MB";
        }
    }

    public string SpaceTotalText
    {
        get
        {
            if (_extractedBytes <= 0) return string.Empty;
            var extractMb = Math.Max(1, (_extractedBytes + 1024L * 1024 - 1) / (1024 * 1024));
            var rollbackMb = HasDbRollback ? (_dbRollbackBytes + 1024L * 1024 - 1) / (1024 * 1024) : 0;
            return $"~{extractMb + rollbackMb:N0} MB";
        }
    }

    public bool ShowNothingToReport =>
        !IsLoading && !HasLoadError && !HasBackupFiles && !ShowDatabasesSection && !ShowProcessesSection && !ShowTasksSection;

    public bool RestoreFiles
    {
        get => _restoreFiles;
        set
        {
            if (SetProperty(ref _restoreFiles, value))
                RaisePropertyChanged(nameof(ShowSkipFileSafetyCopy));
        }
    }

    public bool RestoreDatabases
    {
        get => _restoreDatabases;
        set
        {
            if (SetProperty(ref _restoreDatabases, value))
            {
                RaisePropertyChanged(nameof(ShowSkipDbSafetyCopy));
                RaisePropertyChanged(nameof(DatabaseSummaryItems));
                RaisePropertyChanged(nameof(ShowDatabasesSummary));
            }
        }
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

    public bool SkipFileSafetyCopy
    {
        get => _skipFileSafetyCopy;
        set => SetProperty(ref _skipFileSafetyCopy, value);
    }

    public bool SkipDbSafetyCopy
    {
        get => _skipDbSafetyCopy;
        set
        {
            if (SetProperty(ref _skipDbSafetyCopy, value))
            {
                RaisePropertyChanged(nameof(HasDbRollback));
                RaisePropertyChanged(nameof(SpaceRollbackText));
                RaisePropertyChanged(nameof(SpaceTotalText));
            }
        }
    }

    public bool ShowSkipFileSafetyCopy => RestoreFiles && HasBackupFiles;
    public bool ShowSkipDbSafetyCopy => RestoreDatabases && HasBackupDatabases;

    // Summary panel: only show databases that will actually be acted on
    public IEnumerable<RollbackDeltaItemViewModel> DatabaseSummaryItems
    {
        get
        {
            if (!RestoreDatabases)
                return DatabaseItems.Where(i => i.IsDelete && i.WillDelete);
            return DatabaseItems.Where(i => i.IsDelete ? i.WillDelete : i.IsIncluded);
        }
    }

    public bool ShowDatabasesSummary =>
        (RestoreDatabases && HasBackupDatabases && DatabaseItems.Any(i => !i.IsDelete && i.IsIncluded))
        || DatabaseItems.Any(i => i.IsDelete && i.WillDelete);

    public ICommand RestoreCommand { get; }
    public ICommand CancelCommand { get; }

    public void ApplyDelta(SiteRestoreDelta delta, long extractedBytes, long dbRollbackBytes)
    {
        HasBackupFiles = delta.HasBackupFiles;
        HasBackupDatabases = delta.HasBackupDatabases;
        HasBackupProcesses = delta.HasBackupProcesses;
        HasBackupScheduledTasks = delta.HasBackupScheduledTasks;

        _restoreFiles = delta.HasBackupFiles;
        _restoreDatabases = delta.HasBackupDatabases;
        _restoreProcesses = delta.HasBackupProcesses || delta.Processes.Count > 0;
        _restoreScheduledTasks = delta.HasBackupScheduledTasks || delta.ScheduledTasks.Count > 0;

        _extractedBytes = extractedBytes;
        _dbRollbackBytes = dbRollbackBytes;

        foreach (var db in delta.Databases)
        {
            var item = new RollbackDeltaItemViewModel(db.Name, db.Name, db.Action, supportsIncludeToggle: true);
            item.PropertyChanged += (_, _) =>
            {
                RaisePropertyChanged(nameof(DatabaseSummaryItems));
                RaisePropertyChanged(nameof(ShowDatabasesSummary));
            };
            DatabaseItems.Add(item);
        }

        foreach (var p in delta.Processes)
            ProcessItems.Add(new RollbackDeltaItemViewModel(p.ProcessId, p.Name, p.Action));

        foreach (var t in delta.ScheduledTasks)
            TaskItems.Add(new RollbackDeltaItemViewModel(t.TaskId, t.Label, t.Action));

        IsLoading = false;

        RaisePropertyChanged(nameof(HasBackupFiles));
        RaisePropertyChanged(nameof(HasBackupDatabases));
        RaisePropertyChanged(nameof(HasBackupProcesses));
        RaisePropertyChanged(nameof(HasBackupScheduledTasks));
        RaisePropertyChanged(nameof(RestoreFiles));
        RaisePropertyChanged(nameof(RestoreDatabases));
        RaisePropertyChanged(nameof(RestoreProcesses));
        RaisePropertyChanged(nameof(RestoreScheduledTasks));
        RaisePropertyChanged(nameof(HasDatabaseItems));
        RaisePropertyChanged(nameof(HasProcessItems));
        RaisePropertyChanged(nameof(HasTaskItems));
        RaisePropertyChanged(nameof(HasNoDatabaseItems));
        RaisePropertyChanged(nameof(HasNoProcessItems));
        RaisePropertyChanged(nameof(HasNoTaskItems));
        RaisePropertyChanged(nameof(ShowDatabasesSection));
        RaisePropertyChanged(nameof(ShowProcessesSection));
        RaisePropertyChanged(nameof(ShowTasksSection));
        RaisePropertyChanged(nameof(ShowNothingToReport));
        RaisePropertyChanged(nameof(HasSpaceEstimate));
        RaisePropertyChanged(nameof(HasDbRollback));
        RaisePropertyChanged(nameof(SpaceExtractionText));
        RaisePropertyChanged(nameof(SpaceRollbackText));
        RaisePropertyChanged(nameof(SpaceTotalText));
        RaisePropertyChanged(nameof(ShowSkipFileSafetyCopy));
        RaisePropertyChanged(nameof(ShowSkipDbSafetyCopy));
        RaisePropertyChanged(nameof(DatabaseSummaryItems));
        RaisePropertyChanged(nameof(ShowDatabasesSummary));
    }

    public void SetLoadError(string message)
    {
        LoadError = message;
        IsLoading = false;
    }

    public SiteRestoreOptions BuildRestoreOptions()
    {
        var dbNamesToRestore = (RestoreDatabases && HasBackupDatabases)
            ? DatabaseItems.Where(i => !i.IsDelete && i.IsIncluded).Select(i => i.Id).ToList()
            : null;

        return new SiteRestoreOptions(
            RestoreFiles: RestoreFiles && HasBackupFiles,
            RestoreDatabases: RestoreDatabases && HasBackupDatabases && dbNamesToRestore?.Count != 0,
            RestoreProcesses: RestoreProcesses && (HasBackupProcesses || ProcessItems.Any()),
            RestoreScheduledTasks: RestoreScheduledTasks && (HasBackupScheduledTasks || TaskItems.Any()),
            SkipFileSafetyCopy: _skipFileSafetyCopy,
            SkipDbSafetyCopy: _skipDbSafetyCopy,
            DatabaseNamesToRestore: dbNamesToRestore);
    }

    public SiteRollbackDeletions BuildDeletions() => new(
        DatabaseNamesToDelete: DatabaseItems
            .Where(i => i.IsDelete && i.WillDelete)
            .Select(i => i.Id)
            .ToList(),
        ProcessIdsToDelete: ProcessItems
            .Where(i => i.IsDelete && i.WillDelete)
            .Select(i => i.Id)
            .ToList(),
        TaskIdsToDelete: TaskItems
            .Where(i => i.IsDelete && i.WillDelete)
            .Select(i => i.Id)
            .ToList());

    private static string FormatDate(string iso)
    {
        if (DateTimeOffset.TryParse(iso, out var dt))
            return dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        return iso;
    }
}
