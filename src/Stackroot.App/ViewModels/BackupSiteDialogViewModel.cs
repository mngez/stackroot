using System.Collections.ObjectModel;
using Stackroot.App.Commands;
using Stackroot.App.Localization;
using Stackroot.Core.Databases;
using Stackroot.Core.Sites.Models;
using Stackroot.Core.Supervisor;

namespace Stackroot.App.ViewModels;

public sealed class BackupSiteDialogViewModel : ViewModelBase
{
    public BackupSiteDialogViewModel(
        Site site,
        DatabaseManager databaseManager,
        GlobalProcessManager processManager,
        Scheduling.TaskSchedulerService scheduler)
    {
        SiteDomain = site.Domain;

        var linkedDbs = databaseManager.List()
            .Where(db => string.Equals(db.SiteId, site.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var linkedProcesses = processManager.List(site.Id);
        var linkedTasks = scheduler.List()
            .Where(t => string.Equals(t.SiteId, site.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();

        HasDatabases = linkedDbs.Count > 0;
        HasProcesses = linkedProcesses.Count > 0;
        HasScheduledTasks = linkedTasks.Count > 0;

        DatabaseCount = linkedDbs.Count;
        ProcessCount = linkedProcesses.Count;
        ScheduledTaskCount = linkedTasks.Count;

        IncludeFiles = true;
        IncludeDatabases = HasDatabases;
        IncludeProcesses = HasProcesses;
        IncludeScheduledTasks = HasScheduledTasks;
        DeleteSiteAfterBackup = false;
        SkipSymbolicLinks = true;
        UseCustomIgnore = false;

        StartCommand = new RelayCommand(_ => RequestClose?.Invoke(this, true));
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(this, false));
        AddPatternCommand = new RelayCommand(_ => CustomPatterns.Add(new IgnorePatternEntry(this, string.Empty)));
        AddPresetCommand = new RelayCommand(parameter => CustomPatterns.Add(new IgnorePatternEntry(this, (string)parameter!)));
    }

    public string SiteDomain { get; }

    public bool HasDatabases { get; }
    public bool HasProcesses { get; }
    public bool HasScheduledTasks { get; }
    public int DatabaseCount { get; }
    public int ProcessCount { get; }
    public int ScheduledTaskCount { get; }

    private bool _includeFiles;
    public bool IncludeFiles { get => _includeFiles; set => SetProperty(ref _includeFiles, value); }

    private bool _includeDatabases;
    public bool IncludeDatabases { get => _includeDatabases; set => SetProperty(ref _includeDatabases, value); }

    private bool _includeProcesses;
    public bool IncludeProcesses { get => _includeProcesses; set => SetProperty(ref _includeProcesses, value); }

    private bool _includeScheduledTasks;
    public bool IncludeScheduledTasks { get => _includeScheduledTasks; set => SetProperty(ref _includeScheduledTasks, value); }

    private bool _deleteSiteAfterBackup;
    public bool DeleteSiteAfterBackup
    {
        get => _deleteSiteAfterBackup;
        set
        {
            if (SetProperty(ref _deleteSiteAfterBackup, value))
            {
                RaisePropertyChanged(nameof(ShowDeletePreview));
                RaisePropertyChanged(nameof(ShowBackupOnlyPreview));
                RaisePropertyChanged(nameof(StartLabel));
            }
        }
    }

    public bool ShowDeletePreview => DeleteSiteAfterBackup;
    public bool ShowBackupOnlyPreview => !DeleteSiteAfterBackup;

    private bool _skipSymbolicLinks;
    public bool SkipSymbolicLinks
    {
        get => _skipSymbolicLinks;
        set
        {
            if (SetProperty(ref _skipSymbolicLinks, value))
            {
                RaisePropertyChanged(nameof(ShowSymbolicLinksWarning));
            }
        }
    }

    public bool ShowSymbolicLinksWarning => !SkipSymbolicLinks;

    private bool _useCustomIgnore;
    public bool UseCustomIgnore { get => _useCustomIgnore; set => SetProperty(ref _useCustomIgnore, value); }

    public ObservableCollection<IgnorePatternEntry> CustomPatterns { get; } = [];

    public RelayCommand AddPatternCommand { get; }
    public RelayCommand AddPresetCommand { get; }

    public string StartLabel => DeleteSiteAfterBackup
        ? LocalizationManager.Get("Loc.BackupSiteDialog.StartArchive", "Archive site")
        : LocalizationManager.Get("Loc.BackupSiteDialog.Start", "Backup");

    public RelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }

    public event EventHandler<bool>? RequestClose;

    public Services.SiteBackupOptions BuildOptions() => new(
        IncludeFiles: IncludeFiles,
        IncludeDatabases: IncludeDatabases && HasDatabases,
        IncludeProcesses: IncludeProcesses && HasProcesses,
        IncludeScheduledTasks: IncludeScheduledTasks && HasScheduledTasks,
        DeleteSiteAfterBackup: DeleteSiteAfterBackup,
        SkipSymbolicLinks: SkipSymbolicLinks,
        IgnorePatterns: UseCustomIgnore
            ? CustomPatterns.Select(p => p.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList()
            : []);
}

public sealed class IgnorePatternEntry : ViewModelBase
{
    private readonly BackupSiteDialogViewModel _owner;

    public IgnorePatternEntry(BackupSiteDialogViewModel owner, string text)
    {
        _owner = owner;
        _text = text;
        RemoveCommand = new RelayCommand(_ => _owner.CustomPatterns.Remove(this));
    }

    private string _text;
    public string Text { get => _text; set => SetProperty(ref _text, value); }

    public RelayCommand RemoveCommand { get; }
}
