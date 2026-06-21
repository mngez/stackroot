using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Stackroot.App.Commands;
using Stackroot.App.Helpers;
using Stackroot.App.Services;
using Stackroot.App.Views;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Sites.Management;
using Stackroot.Core.Supervisor;
using Stackroot.Core.Windows;

namespace Stackroot.App.ViewModels;

public sealed class ProcessesViewModel : ViewModelBase, IDisposable
{
    public const string GlobalSiteFilter = "__global__";

    private readonly GlobalProcessManager _processManager;
    private readonly GlobalProcessStore _store;
    private readonly SiteManager _siteManager;
    private readonly IServiceProvider _services;
    private readonly SessionActivityCoordinator _activityCoordinator;
    private readonly SessionActivityReporter _activity;
    private readonly RuntimeStateService _runtimeState;
    private string? _siteFilter;
    private string? _errorMessage;
    private bool _isBulkBusy;
    private string? _busyProcessId;
    private bool _suppressSiteFilterRefresh;
    private int _refreshVersion;
    private int _refreshInFlight;
    private string? _lastQuietSnapshot;

    public ProcessesViewModel(
        GlobalProcessManager processManager,
        GlobalProcessStore store,
        SiteManager siteManager,
        IServiceProvider services,
        SessionActivityCoordinator activityCoordinator,
        SessionActivityReporter activity,
        RuntimeStateService runtimeState)
    {
        _processManager = processManager;
        _store = store;
        _siteManager = siteManager;
        _services = services;
        _activityCoordinator = activityCoordinator;
        _activity = activity;
        _runtimeState = runtimeState;

        Processes = new ObservableCollection<ProcessRowViewModel>();
        SiteFilterOptions = new ObservableCollection<SiteFilterOptionViewModel>();

        RefreshCommand = new RelayCommand(_ => Refresh());
        AddCommand = new RelayCommand(_ => OpenAddDialog());
        StartAllCommand = new RelayCommand(_ => _ = StartAllAsync(), _ => !IsBulkBusy);
        StopAllCommand = new RelayCommand(_ => _ = StopAllAsync(), _ => !IsBulkBusy);
        StartCommand = new RelayCommand(id => _ = StartAsync(id as string), CanStartProcess);
        StopCommand = new RelayCommand(id => _ = StopAsync(id as string), id => !IsProcessBusy(id as string));
        RestartCommand = new RelayCommand(id => _ = RestartAsync(id as string), CanRestartProcess);
        EditCommand = new RelayCommand(row => OpenEditDialog(row as ProcessRowViewModel));
        DeleteCommand = new RelayCommand(row => Delete(row as ProcessRowViewModel));
        ViewLogCommand = new RelayCommand(row => OpenLogDialog(row as ProcessRowViewModel));
        ToggleEnabledCommand = new RelayCommand(row => ToggleField(row as ProcessRowViewModel, p => p with { Enabled = !p.Enabled }));
        ToggleAutoStartCommand = new RelayCommand(row => ToggleField(row as ProcessRowViewModel, p => p with { AutoStart = !p.AutoStart }));
        ToggleFeaturedCommand = new RelayCommand(row => ToggleField(row as ProcessRowViewModel, p => p with { Featured = !(p.Featured == true) }));

        _runtimeState.StateUpdated += OnRuntimeStateUpdated;

        RebuildSiteFilterOptions();
        _ = RefreshAsync();
    }

    public void BeginLoading()
    {
        _runtimeState.SetDetailedPolling(enabled: true);
    }

    public void EndLoading()
    {
        _runtimeState.SetDetailedPolling(enabled: false);
    }

    private void OnRuntimeStateUpdated(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Refresh(quiet: true);
            return;
        }

        dispatcher.BeginInvoke(() => Refresh(quiet: true), DispatcherPriority.Background);
    }

    public ObservableCollection<ProcessRowViewModel> Processes { get; }
    public ObservableCollection<SiteFilterOptionViewModel> SiteFilterOptions { get; }

    public string? SiteFilter
    {
        get => _siteFilter;
        set
        {
            if (SetProperty(ref _siteFilter, value))
            {
                if (!_suppressSiteFilterRefresh)
                {
                    Refresh(quiet: true);
                }
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public bool IsBulkBusy
    {
        get => _isBulkBusy;
        private set
        {
            if (SetProperty(ref _isBulkBusy, value))
            {
                StartAllCommand.RaiseCanExecuteChanged();
                StopAllCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasProcesses => Processes.Count > 0;
    public bool ShowEmptyState => !HasProcesses;

    public RelayCommand RefreshCommand { get; }
    public RelayCommand AddCommand { get; }
    public RelayCommand StartAllCommand { get; }
    public RelayCommand StopAllCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand RestartCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ViewLogCommand { get; }
    public ICommand ToggleEnabledCommand { get; }
    public ICommand ToggleAutoStartCommand { get; }
    public ICommand ToggleFeaturedCommand { get; }

    public void Refresh(bool quiet = false) => _ = RefreshAsync(quiet);

    private async Task RefreshAsync(bool quiet = false)
    {
        var version = Interlocked.Increment(ref _refreshVersion);
        if (quiet)
        {
            if (Interlocked.CompareExchange(ref _refreshInFlight, 1, 0) != 0)
            {
                return;
            }
        }

        if (!quiet)
        {
            RebuildSiteFilterOptions();
        }

        try
        {
            var siteFilter = _siteFilter;
            var processes = await Task.Run(() =>
                ListFilteredProcesses(siteFilter)
                    .OrderBy(process => process.Featured == true ? 0 : 1)
                    .ThenBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList()).ConfigureAwait(true);

            if (version != _refreshVersion)
            {
                return;
            }

            var snapshot = BuildProcessSnapshot(processes);
            if (quiet && string.Equals(snapshot, _lastQuietSnapshot, StringComparison.Ordinal))
            {
                UpdateUptimeTexts(processes);
                return;
            }

            _lastQuietSnapshot = snapshot;

            var rows = processes.Select(ToRow).ToList();

            SyncProcessRows(rows);

            UpdateUptimeTexts(processes);

            ErrorMessage = null;
            RaisePropertyChanged(nameof(HasProcesses));
            RaisePropertyChanged(nameof(ShowEmptyState));
            RaiseCommandStates();
        }
        catch (Exception ex)
        {
            if (!quiet)
            {
                ErrorMessage = ex.Message;
            }
        }
        finally
        {
            if (quiet)
            {
                Interlocked.Exchange(ref _refreshInFlight, 0);
            }
        }
    }

    public void Dispose()
    {
        _runtimeState.StateUpdated -= OnRuntimeStateUpdated;
    }

    private void RebuildSiteFilterOptions()
    {
        var selected = _siteFilter;
        _suppressSiteFilterRefresh = true;
        try
        {
            SiteFilterOptions.Clear();
            SiteFilterOptions.Add(new SiteFilterOptionViewModel(string.Empty, "All sites"));
            SiteFilterOptions.Add(new SiteFilterOptionViewModel(GlobalSiteFilter, "App-wide only"));

            var siteIdsWithProcesses = _processManager.List()
                .Select(process => process.SiteId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var site in _siteManager.List().OrderBy(site => site.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (siteIdsWithProcesses.Contains(site.Id))
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

    private IReadOnlyList<ProcessInfo> ListFilteredProcesses() => ListFilteredProcesses(_siteFilter);

    private IReadOnlyList<ProcessInfo> ListFilteredProcesses(string? siteFilter)
    {
        if (string.Equals(siteFilter, GlobalSiteFilter, StringComparison.Ordinal))
        {
            return _processManager.List()
                .Where(process => string.IsNullOrWhiteSpace(process.SiteId))
                .ToList();
        }

        if (string.IsNullOrWhiteSpace(siteFilter))
        {
            return _processManager.List();
        }

        return _processManager.List(siteFilter);
    }

    private string? ResolveBulkSiteFilter()
    {
        if (string.IsNullOrWhiteSpace(SiteFilter) || string.Equals(SiteFilter, GlobalSiteFilter, StringComparison.Ordinal))
        {
            return string.Equals(SiteFilter, GlobalSiteFilter, StringComparison.Ordinal) ? string.Empty : null;
        }

        return SiteFilter;
    }

    private async Task StartAsync(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        SetProcessBusy(id);
        try
        {
            var result = await Task.Run(() => _processManager.Start(id)).ConfigureAwait(true);
            _activityCoordinator.NotifyProcessAction(result, "start");
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            _activity.LogError("Processes", ex.Message, ex);
            ErrorMessage = ex.Message;
        }
        finally
        {
            ClearProcessBusy(id);
            await RefreshAsync(quiet: true);
            RaiseCommandStates();
        }
    }

    private async Task StopAsync(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        SetProcessBusy(id);
        try
        {
            var result = await Task.Run(() => _processManager.Stop(id)).ConfigureAwait(true);
            _activityCoordinator.NotifyProcessAction(result, "stop");
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            _activity.LogError("Processes", ex.Message, ex);
            ErrorMessage = ex.Message;
        }
        finally
        {
            ClearProcessBusy(id);
            await RefreshAsync(quiet: true);
            RaiseCommandStates();
        }
    }

    private async Task RestartAsync(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        SetProcessBusy(id);
        try
        {
            var result = await Task.Run(() =>
            {
                _processManager.Stop(id);
                Thread.Sleep(300);
                return _processManager.Start(id);
            }).ConfigureAwait(true);
            _activityCoordinator.NotifyProcessAction(result, "restart");
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            _activity.LogError("Processes", ex.Message, ex);
            ErrorMessage = ex.Message;
        }
        finally
        {
            ClearProcessBusy(id);
            await RefreshAsync(quiet: true);
            RaiseCommandStates();
        }
    }

    private async Task StartAllAsync()
    {
        IsBulkBusy = true;
        try
        {
            var filter = ResolveBulkSiteFilter();
            var results = await Task.Run(() => _processManager.StartAll(filter)).ConfigureAwait(true);
            _activityCoordinator.NotifyProcessActions(results, "start");
            if (results.Count > 0)
            {
                _activity.LogSuccess("Processes", SessionActivityMessages.ProcessBulkStarted(results.Count));
            }

            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            _activity.LogError("Processes", ex.Message, ex);
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBulkBusy = false;
            await RefreshAsync(quiet: true);
        }
    }

    private async Task StopAllAsync()
    {
        IsBulkBusy = true;
        try
        {
            var filter = ResolveBulkSiteFilter();
            var results = await Task.Run(() => _processManager.StopAll(filter)).ConfigureAwait(true);
            _activityCoordinator.NotifyProcessActions(results, "stop");
            if (results.Count > 0)
            {
                _activity.LogSuccess("Processes", SessionActivityMessages.ProcessBulkStopped(results.Count));
            }

            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            _activity.LogError("Processes", ex.Message, ex);
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBulkBusy = false;
            await RefreshAsync(quiet: true);
        }
    }

    private void ToggleField(ProcessRowViewModel? row, Func<GlobalProcess, GlobalProcess> patch)
    {
        if (row is null)
        {
            return;
        }

        UpdateToggle(row.Id, patch);
    }

    private void UpdateToggle(string id, Func<GlobalProcess, GlobalProcess> patch) =>
        _ = UpdateToggleAsync(id, patch);

    private async Task UpdateToggleAsync(string id, Func<GlobalProcess, GlobalProcess> patch)
    {
        try
        {
            var existing = _store.GetById(id);
            if (existing is null)
            {
                return;
            }

            await Task.Run(() => _processManager.Update(id, patch(existing))).ConfigureAwait(true);
            ErrorMessage = null;
            await RefreshAsync(quiet: true);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            await RefreshAsync(quiet: true);
        }
    }

    private void Delete(ProcessRowViewModel? row) =>
        _ = DeleteAsync(row);

    private async Task DeleteAsync(ProcessRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var owner = Application.Current?.MainWindow;
        if (!ConfirmDialog.Show(owner, "Remove process?", $"Remove \"{row.Name}\"?", "Remove", isDanger: true))
        {
            return;
        }

        try
        {
            await Task.Run(() => _processManager.Remove(row.Id)).ConfigureAwait(true);
            _activity.LogInfo("Processes", SessionActivityMessages.ProcessRemoved(row.Name));
            ErrorMessage = null;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private void OpenAddDialog()
    {
        OpenProcessDialog(editProcess: null);
    }

    private void OpenEditDialog(ProcessRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var process = _processManager.List().FirstOrDefault(candidate => string.Equals(candidate.Id, row.Id, StringComparison.Ordinal));
        if (process is null)
        {
            return;
        }

        OpenProcessDialog(process);
    }

    private void OpenProcessDialog(ProcessInfo? editProcess)
    {
        var dialogVm = editProcess is null
            ? ActivatorUtilities.CreateInstance<AddGlobalProcessDialogViewModel>(_services)
            : ActivatorUtilities.CreateInstance<AddGlobalProcessDialogViewModel>(_services, editProcess);
        var owner = Application.Current?.MainWindow;
        var dialog = new AddGlobalProcessDialog
        {
            DataContext = dialogVm,
            Owner = owner
        };

        dialogVm.RequestClose += (_, _) => dialog.Close();
        dialogVm.Saved += (_, _) => Refresh();
        dialog.ShowDialog();
    }

    private void OpenLogDialog(ProcessRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var dialogVm = new SiteProcessLogDialogViewModel(_processManager, row.Id, row.Name);
        var owner = Application.Current?.MainWindow;
        var dialog = new SiteProcessLogDialog
        {
            DataContext = dialogVm,
            Owner = owner
        };

        dialogVm.RequestClose += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => dialogVm.Dispose();
        dialog.Show();
    }

    private bool IsProcessBusy(string? id) =>
        !string.IsNullOrWhiteSpace(id) && string.Equals(_busyProcessId, id, StringComparison.Ordinal);

    private bool CanStartProcess(object? id)
    {
        if (IsProcessBusy(id as string))
        {
            return false;
        }

        var processId = id as string;
        if (string.IsNullOrWhiteSpace(processId))
        {
            return false;
        }

        var process = Processes.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, processId, StringComparison.Ordinal));
        return process is { CanStart: true } && !process.IsActive;
    }

    private bool CanRestartProcess(object? id)
    {
        if (IsProcessBusy(id as string))
        {
            return false;
        }

        var processId = id as string;
        if (string.IsNullOrWhiteSpace(processId))
        {
            return false;
        }

        var process = Processes.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, processId, StringComparison.Ordinal));
        return process is { Enabled: true, IsActive: true };
    }

    private void SetProcessBusy(string id)
    {
        _busyProcessId = id;
        foreach (var row in Processes)
        {
            row.IsBusy = string.Equals(row.Id, id, StringComparison.Ordinal);
        }

        RaiseCommandStates();
    }

    private void ClearProcessBusy(string id)
    {
        if (string.Equals(_busyProcessId, id, StringComparison.Ordinal))
        {
            _busyProcessId = null;
        }

        foreach (var row in Processes)
        {
            if (string.Equals(row.Id, id, StringComparison.Ordinal))
            {
                row.IsBusy = false;
                break;
            }
        }

        RaiseCommandStates();
    }

    private void RaiseCommandStates()
    {
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        RestartCommand.RaiseCanExecuteChanged();
    }

    private void SyncProcessRows(IReadOnlyList<ProcessRowViewModel> incoming)
    {
        var incomingIds = incoming.Select(row => row.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var i = Processes.Count - 1; i >= 0; i--)
        {
            if (!incomingIds.Contains(Processes[i].Id))
            {
                Processes.RemoveAt(i);
            }
        }

        for (var index = 0; index < incoming.Count; index++)
        {
            var row = incoming[index];
            var existingIndex = -1;
            for (var i = 0; i < Processes.Count; i++)
            {
                if (string.Equals(Processes[i].Id, row.Id, StringComparison.OrdinalIgnoreCase))
                {
                    existingIndex = i;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                var existing = Processes[existingIndex];
                existing.ApplyLiveUpdate(row);
                if (existingIndex != index)
                {
                    Processes.RemoveAt(existingIndex);
                    Processes.Insert(index, existing);
                }
            }
            else
            {
                Processes.Insert(index, row);
            }
        }
    }

    private static string BuildProcessSnapshot(IReadOnlyList<ProcessInfo> processes) =>
        string.Join('\n', processes.Select(process =>
            string.Join('|',
                process.Id,
                process.Status,
                process.Available,
                process.Pid?.ToString() ?? string.Empty,
                process.Enabled,
                process.AutoStart,
                process.Featured == true,
                process.CommandLine,
                process.Message ?? string.Empty,
                process.ResolvedCwd ?? process.WorkDir ?? string.Empty)));

    private ProcessRowViewModel ToRow(ProcessInfo process)
    {
        var site = !string.IsNullOrWhiteSpace(process.SiteId) ? _siteManager.Get(process.SiteId) : null;
        var row = new ProcessRowViewModel
        {
            Id = process.Id,
            Name = process.Name,
            SiteLabel = site is null
                ? (string.IsNullOrWhiteSpace(process.SiteId) ? "App-wide" : process.SiteId!)
                : $"{site.Name} · {site.Domain}",
            RuntimeLabel = process.RuntimeLabel,
            StatusText = FormatStatus(process),
            StatusColor = FormatStatusColor(process),
            IndicatorColor = FormatIndicatorColor(process),
            RowBorderBrush = FormatRowBorderBrush(process),
            IsLive = process.Status is ProcessStatus.Running or ProcessStatus.Restarting,
            Pid = process.Pid?.ToString() ?? "-",
            CommandLine = process.CommandLine,
            WorkDir = process.ResolvedCwd ?? process.WorkDir ?? string.Empty,
            Message = process.Message ?? string.Empty,
            Enabled = process.Enabled,
            AutoStart = process.AutoStart,
            Featured = process.Featured == true,
            IsActive = process.Status is ProcessStatus.Running or ProcessStatus.Restarting,
            CanStart = process.Enabled && process.Available,
            ShowLog = process.HasLog == true || process.Status is ProcessStatus.Running or ProcessStatus.Error
        };

        row.SetUptimeFromPid(process.Status is ProcessStatus.Running or ProcessStatus.Restarting ? process.Pid : null);
        return row;
    }

    private static void UpdateUptimeTexts(IReadOnlyList<ProcessInfo> processes, IEnumerable<ProcessRowViewModel> rows)
    {
        foreach (var row in rows)
        {
            var process = processes.FirstOrDefault(candidate => candidate.Id == row.Id);
            row.SetUptimeFromPid(process?.Status is ProcessStatus.Running or ProcessStatus.Restarting ? process.Pid : null);
        }
    }

    private void UpdateUptimeTexts(IReadOnlyList<ProcessInfo> processes) =>
        UpdateUptimeTexts(processes, Processes);

    private static string FormatStatus(ProcessInfo process)
    {
        if (process.Status is ProcessStatus.Running or ProcessStatus.Restarting)
        {
            return process.Status == ProcessStatus.Restarting ? "Restarting" : "Running";
        }

        if (!process.Available)
        {
            return "Unavailable";
        }

        return process.Status switch
        {
            ProcessStatus.Error => "Error",
            _ => "Stopped"
        };
    }

    private static string FormatStatusColor(ProcessInfo process) =>
        process.Status switch
        {
            ProcessStatus.Running => "#8FD6B6",
            ProcessStatus.Restarting => "#9BB8F0",
            ProcessStatus.Error => "#EAAAB0",
            _ => "#91A0B5"
        };

    private static string FormatIndicatorColor(ProcessInfo process) =>
        process.Status switch
        {
            ProcessStatus.Running => "#4CAE8C",
            ProcessStatus.Restarting => "#E9BD5B",
            ProcessStatus.Error => "#E88A92",
            _ => "#91A0B5"
        };

    private static string FormatRowBorderBrush(ProcessInfo process) =>
        process.Status switch
        {
            ProcessStatus.Running => "#4D4CAE8C",
            ProcessStatus.Restarting => "#66E9BD5B",
            ProcessStatus.Error => "#59E88A92",
            _ => "#263348"
        };
}

public sealed class ProcessRowViewModel : ViewModelBase, IUptimeTooltipTarget
{
    private bool _isBusy;
    private string _uptimeText = string.Empty;
    private int? _uptimePid;
    private string _statusText = "Stopped";
    private string _statusColor = "#91A0B5";
    private string _indicatorColor = "#91A0B5";
    private string _rowBorderBrush = "#263348";
    private string _pid = "-";
    private string _message = string.Empty;
    private bool _isActive;
    private bool _isLive;
    private bool _canStart;
    private bool _showLog;

    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string SiteLabel { get; init; } = string.Empty;
    public string RuntimeLabel { get; init; } = string.Empty;

    public string StatusText
    {
        get => _statusText;
        set
        {
            if (SetProperty(ref _statusText, value))
            {
                RaisePropertyChanged(nameof(StatusBrush));
            }
        }
    }

    public string StatusColor
    {
        get => _statusColor;
        set
        {
            if (SetProperty(ref _statusColor, value))
            {
                RaisePropertyChanged(nameof(StatusBrush));
            }
        }
    }

    public string IndicatorColor
    {
        get => _indicatorColor;
        set
        {
            if (SetProperty(ref _indicatorColor, value))
            {
                RaisePropertyChanged(nameof(IndicatorBrush));
            }
        }
    }

    public string RowBorderBrush
    {
        get => _rowBorderBrush;
        set
        {
            if (SetProperty(ref _rowBorderBrush, value))
            {
                RaisePropertyChanged(nameof(RowBorder));
            }
        }
    }

    public System.Windows.Media.Brush StatusBrush => CreateBrush(StatusColor);
    public System.Windows.Media.Brush IndicatorBrush => CreateBrush(IndicatorColor);
    public System.Windows.Media.Brush RowBorder => CreateBrush(RowBorderBrush);

    public string Pid
    {
        get => _pid;
        set => SetProperty(ref _pid, value);
    }

    public string CommandLine { get; init; } = string.Empty;
    public string WorkDir { get; init; } = string.Empty;

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public bool Enabled { get; init; }
    public bool AutoStart { get; init; }
    public bool Featured { get; init; }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }

    public bool IsLive
    {
        get => _isLive;
        set => SetProperty(ref _isLive, value);
    }

    public bool CanStart
    {
        get => _canStart;
        set => SetProperty(ref _canStart, value);
    }

    public bool ShowLog
    {
        get => _showLog;
        set => SetProperty(ref _showLog, value);
    }

    public string PinGlyph => Featured ? "\uE735" : "\uE734";
    public System.Windows.Media.Brush PinColor => Featured
        ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE9, 0xBD, 0x5B))
        : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x91, 0xA0, 0xB5));
    public bool ShowBusyIndicator => IsBusy;

    public void ApplyLiveUpdate(ProcessRowViewModel source)
    {
        StatusText = source.StatusText;
        StatusColor = source.StatusColor;
        IndicatorColor = source.IndicatorColor;
        RowBorderBrush = source.RowBorderBrush;
        Pid = source.Pid;
        Message = source.Message;
        IsActive = source.IsActive;
        IsLive = source.IsLive;
        CanStart = source.CanStart;
        ShowLog = source.ShowLog;
        SetUptimeFromPid(source.LiveUptimePid);
    }

    internal int? LiveUptimePid => _uptimePid;

    public string UptimeText
    {
        get => _uptimeText;
        set
        {
            if (SetProperty(ref _uptimeText, value))
            {
                RaisePropertyChanged(nameof(UptimeToolTip));
            }
        }
    }

    public string? UptimeToolTip => ProcessUptime.FormatToolTip(UptimeText);

    public void SetUptimeFromPid(int? pid)
    {
        _uptimePid = pid is > 0 ? pid : null;
        RefreshUptimeDisplay();
    }

    public void RefreshUptimeDisplay()
    {
        UptimeText = _uptimePid is > 0
            ? ProcessUptime.FormatFromPid(_uptimePid) ?? string.Empty
            : string.Empty;
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaisePropertyChanged(nameof(ShowBusyIndicator));
            }
        }
    }

    private static System.Windows.Media.SolidColorBrush CreateBrush(string hex) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);
}

public sealed record SiteFilterOptionViewModel(string Id, string Label);
