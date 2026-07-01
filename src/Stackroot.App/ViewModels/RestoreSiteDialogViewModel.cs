using System.Windows.Input;
using Stackroot.App.Commands;
using Stackroot.App.Services;
using SiteModel = Stackroot.Core.Sites.Models.Site;

namespace Stackroot.App.ViewModels;

public sealed class RestoreSiteDialogViewModel : ViewModelBase
{
    private bool _restoreFiles;
    private bool _restoreDatabases;
    private bool _restoreProcesses;
    private bool _restoreScheduledTasks;

    public RestoreSiteDialogViewModel(SiteModel site, BackupManifest manifest)
    {
        SiteDomain = site.Domain;
        SiteName = manifest.SiteName;
        BackupDate = FormatDate(manifest.BackupDate);
        HasFiles = manifest.Contents.HasFiles;
        HasDatabases = manifest.Contents.HasDatabases;
        HasProcesses = manifest.Contents.HasProcesses;
        HasScheduledTasks = manifest.Contents.HasScheduledTasks;

        _restoreFiles = HasFiles;
        _restoreDatabases = HasDatabases;
        _restoreProcesses = HasProcesses;
        _restoreScheduledTasks = HasScheduledTasks;

        RestoreCommand = new RelayCommand(_ => RequestClose?.Invoke(this, true));
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(this, false));
    }

    public event EventHandler<bool>? RequestClose;

    public string SiteDomain { get; }
    public string SiteName { get; }
    public string BackupDate { get; }
    public bool HasFiles { get; }
    public bool HasDatabases { get; }
    public bool HasProcesses { get; }
    public bool HasScheduledTasks { get; }

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

    public ICommand RestoreCommand { get; }
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
