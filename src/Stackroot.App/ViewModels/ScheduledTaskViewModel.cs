using System.Collections.ObjectModel;
using System.Windows;
using Stackroot.App.Commands;
using Stackroot.App.Scheduling;

namespace Stackroot.App.ViewModels;

public sealed class ScheduledTaskRowViewModel : ViewModelBase
{
    private readonly ScheduledTaskViewModel _parent;
    public ScheduledTaskModel Model { get; }

    public string Id => Model.Id;
    public string Label { get => Model.Label; set { Model.Label = value; RaisePropertyChanged(); _parent.Save(); } }
    public string Command { get => Model.Command; set { Model.Command = value; RaisePropertyChanged(); _parent.Save(); } }
    public string WorkingDirectory { get => Model.WorkingDirectory; set { Model.WorkingDirectory = value; RaisePropertyChanged(); _parent.Save(); } }
    public string CronExpression { get => Model.CronExpression; set { Model.CronExpression = value; RaisePropertyChanged(); _parent.Save(); } }
    public bool CaptureLog { get => Model.CaptureLog; set { Model.CaptureLog = value; RaisePropertyChanged(); _parent.Save(); } }
    public bool IsEnabled { get => Model.IsEnabled; set { Model.IsEnabled = value; RaisePropertyChanged(); _parent.Save(); } }

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

    public ScheduledTaskRowViewModel(ScheduledTaskModel model, ScheduledTaskViewModel parent)
    {
        Model = model;
        _parent = parent;
        RunNowCommand = new RelayCommand(_ => Run(), _ => !IsRunning);
        ViewLogCommand = new RelayCommand(_ => OpenLog(), _ => HasLog);
        DeleteCommand = new RelayCommand(_ => ConfirmDelete());
    }

    private async void Run()
    {
        IsRunning = true;
        await _parent.RunNowAndWaitAsync(Id);
        await System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            IsRunning = false;
            _parent.Load();
        });
    }

    private void ConfirmDelete()
    {
        var result = System.Windows.MessageBox.Show(
            $"Delete scheduled task \"{Label}\"?",
            "Delete task",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (result == System.Windows.MessageBoxResult.Yes)
            _parent.Delete(Id);
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

    private void OpenLog()
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
        // Open the log folder so all historical runs are visible
        var dir = System.IO.Path.GetDirectoryName(Model.LastLogPath);
        if (dir is not null && System.IO.Directory.Exists(dir))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
    }
}

public sealed class ScheduledTaskViewModel : ViewModelBase
{
    private readonly Scheduling.TaskSchedulerService _scheduler;
    private readonly Stackroot.App.Services.SessionActivityReporter _activity;
    private readonly Stackroot.Core.Settings.SettingsStore _settingsStore;

    public ObservableCollection<ScheduledTaskRowViewModel> Tasks { get; } = [];
    public bool HasTasks => Tasks.Count > 0;

    public RelayCommand AddCommand { get; }
    public RelayCommand EditCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public ScheduledTaskViewModel(Scheduling.TaskSchedulerService scheduler,
        Stackroot.App.Services.SessionActivityReporter activity,
        Stackroot.Core.Settings.SettingsStore settingsStore)
    {
        _scheduler = scheduler;
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
        Tasks.Clear();
        foreach (var task in _scheduler.List())
            Tasks.Add(new ScheduledTaskRowViewModel(task, this));
        RaisePropertyChanged(nameof(HasTasks));
    }

    public void Save()
    {
        foreach (var row in Tasks)
            _scheduler.Update(row.Model);
    }

    public void RunNow(string id) => _ = _scheduler.RunNowAsync(id);

    public Task RunNowAndWaitAsync(string id) => _scheduler.RunNowAsync(id);

    public void Delete(string id)
    {
        _scheduler.Delete(id);
        Load();
    }

    private void OpenDialog(ScheduledTaskRowViewModel? existing = null)
    {
        var dlgVm = new CronTaskDialogViewModel(existing?.Model);
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

    public void OpenFileWithEditor(string path)
    {
        var settings = _settingsStore.Load();
        var editor = settings.General.PreferredEditor ?? Stackroot.Core.Abstractions.PreferredEditor.System;
        string? exe = editor switch
        {
            Stackroot.Core.Abstractions.PreferredEditor.Vscode => ResolveInPath("code"),
            Stackroot.Core.Abstractions.PreferredEditor.Cursor => ResolveInPath("cursor"),
            Stackroot.Core.Abstractions.PreferredEditor.NotepadPlusPlus => ResolveInPath("notepad++"),
            Stackroot.Core.Abstractions.PreferredEditor.Custom => settings.General.CustomEditorPath,
            _ => null
        };

        try
        {
            if (!string.IsNullOrWhiteSpace(exe) && System.IO.File.Exists(exe))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = path,
                    UseShellExecute = false
                });
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
        }
        catch { /* best-effort */ }
    }

    private static string? ResolveInPath(string name) =>
        System.IO.File.Exists($@"C:\Program Files\{name}\{name}.exe") ? $@"C:\Program Files\{name}\{name}.exe"
        : System.IO.File.Exists($@"C:\Users\{Environment.UserName}\AppData\Local\Programs\{name}\{name}.exe") ? $@"C:\Users\{Environment.UserName}\AppData\Local\Programs\{name}\{name}.exe"
        : null;
}
