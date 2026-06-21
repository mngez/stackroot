using System.Collections.ObjectModel;
using System.Windows;
using Stackroot.App.Commands;
using Stackroot.App.Scheduling;
using Stackroot.Core.Sites.Management;

namespace Stackroot.App.ViewModels;

public sealed class CronFieldOption
{
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}

public sealed class CronTaskDialogViewModel : ViewModelBase
{
    private readonly SiteManager _siteManager;
    private string _label = string.Empty;
    private string _command = string.Empty;
    private string _workingDirectory = string.Empty;
    private string? _siteId = string.Empty;
    private bool _captureLog;
    private string _cronExpression = "* * * * *";
    private bool _syncingCron;
    private CronFieldOption _selectedMinute = null!;
    private CronFieldOption _selectedHour = null!;
    private CronFieldOption _selectedDay = null!;
    private CronFieldOption _selectedMonth = null!;
    private CronFieldOption _selectedWeekday = null!;

    public CronTaskDialogViewModel(SiteManager siteManager, ScheduledTaskModel? existing = null, string? defaultSiteId = null)
    {
        _siteManager = siteManager;

        Sites = new ObservableCollection<SiteOptionViewModel>(
        [
            new SiteOptionViewModel(string.Empty, "None — app-wide task"),
            ..siteManager.List()
                .OrderBy(site => site.Name, StringComparer.OrdinalIgnoreCase)
                .Select(site => new SiteOptionViewModel(site.Id, $"{site.Name} ({site.Domain})"))
        ]);

        MinuteOptions =
        [
            new() { Label = "Every minute", Value = "*" },
            new() { Label = "Every 2 minutes", Value = "*/2" },
            new() { Label = "Every 5 minutes", Value = "*/5" },
            new() { Label = "Every 10 minutes", Value = "*/10" },
            new() { Label = "Every 15 minutes", Value = "*/15" },
            new() { Label = "Every 30 minutes", Value = "*/30" },
        ];
        HourOptions = [new() { Label = "Every hour", Value = "*" }];
        DayOptions = [new() { Label = "Every day", Value = "*" }];
        MonthOptions = [new() { Label = "Every month", Value = "*" }];
        WeekdayOptions = [new() { Label = "Every weekday", Value = "*" }];

        for (var i = 0; i < 24; i++)
            HourOptions.Add(new() { Label = i.ToString("00"), Value = i.ToString() });
        for (var i = 1; i <= 31; i++)
            DayOptions.Add(new() { Label = i.ToString(), Value = i.ToString() });
        for (var i = 1; i <= 12; i++)
            MonthOptions.Add(new() { Label = i.ToString(), Value = i.ToString() });
        WeekdayOptions.Add(new() { Label = "Sunday", Value = "0" });
        WeekdayOptions.Add(new() { Label = "Monday", Value = "1" });
        WeekdayOptions.Add(new() { Label = "Tuesday", Value = "2" });
        WeekdayOptions.Add(new() { Label = "Wednesday", Value = "3" });
        WeekdayOptions.Add(new() { Label = "Thursday", Value = "4" });
        WeekdayOptions.Add(new() { Label = "Friday", Value = "5" });
        WeekdayOptions.Add(new() { Label = "Saturday", Value = "6" });

        SelectedMinute = MinuteOptions[0];
        SelectedHour = HourOptions[0];
        SelectedDay = DayOptions[0];
        SelectedMonth = MonthOptions[0];
        SelectedWeekday = WeekdayOptions[0];

        SaveCommand = new RelayCommand(_ => RequestClose?.Invoke(this, true), _ => IsValid);
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(this, false));
        BrowseDirCommand = new RelayCommand(_ => BrowseDirectory());

        // Pre-fill from existing model (after commands are created)
        if (existing is not null)
        {
            _label = existing.Label;
            _command = existing.Command;
            _workingDirectory = existing.WorkingDirectory;
            _siteId = existing.SiteId ?? string.Empty;
            _captureLog = existing.CaptureLog;
            _cronExpression = existing.CronExpression;
            RaisePropertyChanged(nameof(TaskLabel));
            RaisePropertyChanged(nameof(TaskCommand));
            RaisePropertyChanged(nameof(WorkingDirectory));
            RaisePropertyChanged(nameof(SiteId));
            RaisePropertyChanged(nameof(CaptureLog));
            RaisePropertyChanged(nameof(CronExpression));
            RaisePropertyChanged(nameof(CronPreview));
            SyncDropdownsFromCron(existing.CronExpression);
            SaveCommand.RaiseCanExecuteChanged();
        }
        else
        {
            _workingDirectory = string.Empty;
            _cronExpression = "* * * * *";
            if (!string.IsNullOrWhiteSpace(defaultSiteId))
            {
                SiteId = defaultSiteId;
            }
        }
    }

    public ObservableCollection<CronFieldOption> MinuteOptions { get; }
    public ObservableCollection<CronFieldOption> HourOptions { get; }
    public ObservableCollection<CronFieldOption> DayOptions { get; }
    public ObservableCollection<CronFieldOption> MonthOptions { get; }
    public ObservableCollection<CronFieldOption> WeekdayOptions { get; }
    public ObservableCollection<SiteOptionViewModel> Sites { get; }

    public CronFieldOption SelectedMinute
    {
        get => _selectedMinute;
        set { if (SetProperty(ref _selectedMinute, value)) RaiseCronChanged(); }
    }
    public CronFieldOption SelectedHour
    {
        get => _selectedHour;
        set { if (SetProperty(ref _selectedHour, value)) RaiseCronChanged(); }
    }
    public CronFieldOption SelectedDay
    {
        get => _selectedDay;
        set { if (SetProperty(ref _selectedDay, value)) RaiseCronChanged(); }
    }
    public CronFieldOption SelectedMonth
    {
        get => _selectedMonth;
        set { if (SetProperty(ref _selectedMonth, value)) RaiseCronChanged(); }
    }
    public CronFieldOption SelectedWeekday
    {
        get => _selectedWeekday;
        set { if (SetProperty(ref _selectedWeekday, value)) RaiseCronChanged(); }
    }

    public string CronExpression
    {
        get => _cronExpression;
        set
        {
            if (!SetProperty(ref _cronExpression, (value ?? "* * * * *").Trim())) return;
            RaisePropertyChanged(nameof(CronPreview));
            RaisePropertyChanged(nameof(CronError));
            SaveCommand.RaiseCanExecuteChanged();
            if (!_syncingCron) SyncDropdownsFromCron(CronExpression);
        }
    }

    public string CronPreview => CronParser.Describe(CronExpression);

    private void SyncFromDropdowns()
    {
        _syncingCron = true;
        CronExpression = $"{SelectedMinute?.Value ?? "*"} {SelectedHour?.Value ?? "*"} {SelectedDay?.Value ?? "*"} {SelectedMonth?.Value ?? "*"} {SelectedWeekday?.Value ?? "*"}";
        _syncingCron = false;
    }

    private void RaiseCronChanged()
    {
        if (!_syncingCron) SyncFromDropdowns();
    }

    public string TaskLabel { get => _label; set { SetProperty(ref _label, value); SaveCommand.RaiseCanExecuteChanged(); } }
    public string TaskCommand { get => _command; set { SetProperty(ref _command, value); SaveCommand.RaiseCanExecuteChanged(); } }
    public string WorkingDirectory { get => _workingDirectory; set { SetProperty(ref _workingDirectory, value); SaveCommand.RaiseCanExecuteChanged(); } }

    public string? SiteId
    {
        get => _siteId;
        set
        {
            if (!SetProperty(ref _siteId, value))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                var site = _siteManager.Get(value);
                if (site is not null && string.IsNullOrWhiteSpace(_workingDirectory))
                {
                    WorkingDirectory = site.Path;
                }
            }
        }
    }

    public bool CaptureLog { get => _captureLog; set => SetProperty(ref _captureLog, value); }

    public bool IsValid => !string.IsNullOrWhiteSpace(TaskLabel)
                        && !string.IsNullOrWhiteSpace(TaskCommand)
                        && !string.IsNullOrWhiteSpace(WorkingDirectory)
                        && IsCronValid(CronExpression);

    public string? CronError => IsCronValid(CronExpression) ? null : "Invalid cron expression — must be 5 fields: minute hour day month weekday";

    private static bool IsCronValid(string cron)
    {
        if (string.IsNullOrWhiteSpace(cron)) return false;
        var parts = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 5;
    }

    private void SyncDropdownsFromCron(string cron)
    {
        var parts = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) return;

        _syncingCron = true;
        SelectedMinute = MinuteOptions.FirstOrDefault(o => o.Value == parts[0]) ?? MinuteOptions[0];
        SelectedHour = HourOptions.FirstOrDefault(o => o.Value == parts[1]) ?? HourOptions[0];
        SelectedDay = DayOptions.FirstOrDefault(o => o.Value == parts[2]) ?? DayOptions[0];
        SelectedMonth = MonthOptions.FirstOrDefault(o => o.Value == parts[3]) ?? MonthOptions[0];
        SelectedWeekday = WeekdayOptions.FirstOrDefault(o => o.Value == parts[4]) ?? WeekdayOptions[0];
        _syncingCron = false;
    }

    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand BrowseDirCommand { get; }

    public event EventHandler<bool>? RequestClose;

    private void BrowseDirectory()
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select working directory for the task",
            ShowNewFolderButton = true
        };
        if (!string.IsNullOrWhiteSpace(WorkingDirectory) && System.IO.Directory.Exists(WorkingDirectory))
            dlg.SelectedPath = WorkingDirectory;

        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            WorkingDirectory = dlg.SelectedPath;
    }

    public ScheduledTaskModel ToModel(string? existingId = null)
    {
        return new ScheduledTaskModel
        {
            Id = existingId ?? Guid.NewGuid().ToString("N")[..12],
            Label = TaskLabel.Trim(),
            Command = TaskCommand.Trim(),
            CronExpression = CronExpression,
            WorkingDirectory = WorkingDirectory.Trim(),
            CaptureLog = CaptureLog,
            SiteId = string.IsNullOrWhiteSpace(_siteId) ? null : _siteId.Trim()
        };
    }
}
