using System.Collections.ObjectModel;
using System.Windows;
using Stackroot.App.Commands;
using Stackroot.App.Helpers;
using Stackroot.App.Scheduling;
using Stackroot.App.Views;
using Stackroot.Core.Sites.Management;

namespace Stackroot.App.ViewModels;

public sealed class ScheduledTaskRowViewModel : ViewModelBase
{
    private readonly IScheduledTaskRowHost _host;
    public ScheduledTaskModel Model { get; }

    public string Id => Model.Id;
    public string Label { get => Model.Label; set { Model.Label = value; RaisePropertyChanged(); _host.UpdateTask(Model); } }
    public string Command { get => Model.Command; set { Model.Command = value; RaisePropertyChanged(); _host.UpdateTask(Model); } }
    public string WorkingDirectory { get => Model.WorkingDirectory; set { Model.WorkingDirectory = value; RaisePropertyChanged(); _host.UpdateTask(Model); } }
    public string CronExpression { get => Model.CronExpression; set { Model.CronExpression = value; RaisePropertyChanged(); _host.UpdateTask(Model); } }
    public bool CaptureLog { get => Model.CaptureLog; set { Model.CaptureLog = value; RaisePropertyChanged(); _host.UpdateTask(Model); } }
    public bool IsEnabled { get => Model.IsEnabled; set { Model.IsEnabled = value; RaisePropertyChanged(); _host.UpdateTask(Model); } }
    public string SiteLabel { get; }
    public bool ShowSiteLabel { get; }

    public string CronDescription => Scheduling.CronParser.Describe(Model.CronExpression);
    public string LastRunDisplay => Model.LastRunAt is not null
        ? DateTime.Parse(Model.LastRunAt).ToLocalTime().ToString("g")
        : "Never";
    public string StatusDisplay => Model.IsEnabled ? "Active" : "Paused";
    public string StatusColor => Model.IsEnabled ? "#8FD6B6" : "#91A0B5";
    public bool HasError => !string.IsNullOrWhiteSpace(Model.LastError);
    public bool HasLog => !string.IsNullOrWhiteSpace(Model.LastLogPath) && System.IO.File.Exists(Model.LastLogPath);

    public RelayCommand RunNowCommand { get; }
    public RelayCommand ViewLogCommand { get; }
    public RelayCommand DeleteCommand { get; }

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; set { if (SetProperty(ref _isRunning, value)) RaisePropertyChanged(nameof(RunButtonText)); } }
    public string RunButtonText => IsRunning ? "⏳" : "▶";

    public ScheduledTaskRowViewModel(
        ScheduledTaskModel model,
        IScheduledTaskRowHost host,
        SiteManager siteManager,
        bool showSiteLabel = true)
    {
        Model = model;
        _host = host;
        ShowSiteLabel = showSiteLabel;
        SiteLabel = FormatSiteLabel(model.SiteId, siteManager);
        RunNowCommand = new RelayCommand(_ => Run(), _ => !IsRunning);
        ViewLogCommand = new RelayCommand(_ => OpenLog(), _ => HasLog);
        DeleteCommand = new RelayCommand(_ => ConfirmDelete());
    }

    private static string FormatSiteLabel(string? siteId, SiteManager siteManager)
    {
        if (string.IsNullOrWhiteSpace(siteId))
        {
            return "App-wide";
        }

        var site = siteManager.Get(siteId);
        return site is null ? siteId : $"{site.Name} ({site.Domain})";
    }

    private async void Run()
    {
        IsRunning = true;
        await _host.RunNowAndWaitAsync(Id);
        await System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            IsRunning = false;
            _host.ReloadTasks();
        });
    }

    private void ConfirmDelete()
    {
        var owner = Application.Current?.MainWindow;
        if (!ConfirmDialog.Show(
                owner,
                "Delete scheduled task?",
                $"Delete \"{Label}\"? This cannot be undone.",
                "Delete",
                isDanger: true))
        {
            return;
        }

        _host.DeleteTask(Id);
    }

    public void Refresh()
    {
        RaisePropertyChanged(nameof(CronDescription));
        RaisePropertyChanged(nameof(LastRunDisplay));
        RaisePropertyChanged(nameof(StatusDisplay));
        RaisePropertyChanged(nameof(StatusColor));
        RaisePropertyChanged(nameof(HasError));
        RaisePropertyChanged(nameof(HasLog));
        ViewLogCommand.RaiseCanExecuteChanged();
    }

    public void OpenLog(bool openInExternalEditor = false)
    {
        if (string.IsNullOrWhiteSpace(Model.LastLogPath) || !System.IO.File.Exists(Model.LastLogPath))
        {
            System.Windows.MessageBox.Show(
                Model.CaptureLog
                    ? "No log file yet — run the task first."
                    : "Log capture is disabled for this task. Enable 'Capture output' when editing.",
                Model.Label,
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        _host.OpenTaskLog(Model.Id, Model.LastLogPath, Model.Label, openInExternalEditor);
    }
}

public sealed class ScheduledTaskViewModel : ViewModelBase, IScheduledTaskRowHost
{
    private readonly Scheduling.TaskSchedulerService _scheduler;
    private readonly SiteManager _siteManager;
    private readonly Stackroot.App.Services.SessionActivityReporter _activity;
    private readonly Stackroot.Core.Settings.SettingsStore _settingsStore;
    private string? _siteFilter = string.Empty;
    private bool _suppressSiteFilterRefresh;

    public ObservableCollection<ScheduledTaskRowViewModel> Tasks { get; } = [];
    public ObservableCollection<SiteFilterOptionViewModel> SiteFilterOptions { get; } = [];
    public bool HasTasks => Tasks.Count > 0;
    public bool ShowEmptyState => !HasTasks;

    public string? SiteFilter
    {
        get => _siteFilter;
        set
        {
            if (SetProperty(ref _siteFilter, value) && !_suppressSiteFilterRefresh)
            {
                Load();
            }
        }
    }

    public RelayCommand AddCommand { get; }
    public RelayCommand EditCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public ScheduledTaskViewModel(Scheduling.TaskSchedulerService scheduler,
        SiteManager siteManager,
        Stackroot.App.Services.SessionActivityReporter activity,
        Stackroot.Core.Settings.SettingsStore settingsStore)
    {
        _scheduler = scheduler;
        _siteManager = siteManager;
        _activity = activity;
        _settingsStore = settingsStore;

        _scheduler.TaskExecuted += (_, _) =>
        {
            var d = System.Windows.Application.Current?.Dispatcher;
            if (d is not null && !d.HasShutdownStarted)
                d.BeginInvoke(() => { Load(); _activity.LogInfo("Scheduler", "Scheduled task completed."); });
        };

        AddCommand = new RelayCommand(_ => OpenDialog());
        EditCommand = new RelayCommand(row => Edit(row as ScheduledTaskRowViewModel));
        RefreshCommand = new RelayCommand(_ => Load());
        Load();
    }

    public void Load()
    {
        RebuildSiteFilterOptions();

        Tasks.Clear();
        foreach (var task in ListFilteredTasks(_siteFilter))
        {
            Tasks.Add(new ScheduledTaskRowViewModel(task, this, _siteManager));
        }

        RaisePropertyChanged(nameof(HasTasks));
        RaisePropertyChanged(nameof(ShowEmptyState));
    }

    void IScheduledTaskRowHost.UpdateTask(ScheduledTaskModel model) => _scheduler.Update(model);

    Task IScheduledTaskRowHost.RunNowAndWaitAsync(string id) => _scheduler.RunNowAsync(id);

    void IScheduledTaskRowHost.DeleteTask(string id)
    {
        _scheduler.Delete(id);
        Load();
    }

    void IScheduledTaskRowHost.ReloadTasks() => Load();

    void IScheduledTaskRowHost.OpenTaskLog(string taskId, string logPath, string label, bool openInExternalEditor) =>
        OpenTaskLog(taskId, logPath, label, openInExternalEditor);

    private void RebuildSiteFilterOptions()
    {
        var selected = _siteFilter;
        _suppressSiteFilterRefresh = true;
        try
        {
            SiteFilterOptions.Clear();
            SiteFilterOptions.Add(new SiteFilterOptionViewModel(string.Empty, "All sites"));
            SiteFilterOptions.Add(new SiteFilterOptionViewModel(ProcessesViewModel.GlobalSiteFilter, "App-wide only"));

            var siteIdsWithTasks = _scheduler.List()
                .Select(task => task.SiteId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var site in _siteManager.List().OrderBy(site => site.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (siteIdsWithTasks.Contains(site.Id))
                {
                    SiteFilterOptions.Add(new SiteFilterOptionViewModel(site.Id, $"{site.Name} ({site.Domain})"));
                }
            }

            if (!SiteFilterOptions.Any(option => string.Equals(option.Id, selected, StringComparison.Ordinal)))
            {
                selected = string.Empty;
            }

            _siteFilter = selected;
            RaisePropertyChanged(nameof(SiteFilter));
        }
        finally
        {
            _suppressSiteFilterRefresh = false;
        }
    }

    private IEnumerable<ScheduledTaskModel> ListFilteredTasks(string? siteFilter)
    {
        var all = _scheduler.List();
        if (string.Equals(siteFilter, ProcessesViewModel.GlobalSiteFilter, StringComparison.Ordinal))
        {
            return all.Where(task => string.IsNullOrWhiteSpace(task.SiteId));
        }

        if (string.IsNullOrWhiteSpace(siteFilter))
        {
            return all;
        }

        return all.Where(task => string.Equals(task.SiteId, siteFilter, StringComparison.OrdinalIgnoreCase));
    }

    private void OpenDialog(ScheduledTaskRowViewModel? existing = null)
    {
        var dlgVm = new CronTaskDialogViewModel(_siteManager, existing?.Model);
        var owner = System.Windows.Application.Current?.MainWindow;
        var dialog = new Views.CronTaskDialog { DataContext = dlgVm, Owner = owner };
        var result = false;
        dlgVm.RequestClose += (_, r) => { result = r; dialog.Close(); };
        dialog.ShowDialog();

        if (!result) return;

        var model = dlgVm.ToModel(existing?.Model.Id);
        if (existing is not null)
        {
            existing.Model.Label = model.Label;
            existing.Model.Command = model.Command;
            existing.Model.CronExpression = model.CronExpression;
            existing.Model.WorkingDirectory = model.WorkingDirectory;
            existing.Model.CaptureLog = model.CaptureLog;
            existing.Model.SiteId = model.SiteId;
            _scheduler.Update(existing.Model);
            existing.Refresh();
        }
        else
        {
            _scheduler.Add(model);
        }
        Load();
    }

    private void Edit(ScheduledTaskRowViewModel? row)
    {
        if (row is null) return;
        OpenDialog(row);
    }

    public void OpenTaskLog(string taskId, string logPath, string label, bool openInExternalEditor)
    {
        var task = _scheduler.List().FirstOrDefault(t => t.Id == taskId);
        var running = false;
        var session = new SiteLogSession(logPath)
        {
            CommandLine = task?.Command,
            IsRunning = () => running
        };

        if (task is { CaptureLog: true })
        {
            session.RunAgainAsync = async () =>
            {
                running = true;
                session.MarkRunning();
                try
                {
                    await _scheduler.RunNowAsync(taskId).ConfigureAwait(true);
                    Load();
                    var updated = _scheduler.List().FirstOrDefault(t => t.Id == taskId);
                    if (updated?.LastLogPath is { Length: > 0 } newPath && System.IO.File.Exists(newPath))
                    {
                        session.LogPath = newPath;
                        session.CommandLine = updated.Command;
                    }
                }
                finally
                {
                    running = false;
                    session.MarkFinished();
                    session.NotifyUpdated();
                }
            };
        }

        StackrootLogViewer.Open(
            logPath,
            $"Log — {label}",
            openInExternalEditor,
            _settingsStore,
            chrome: new SiteLogChrome(session));
    }
}
