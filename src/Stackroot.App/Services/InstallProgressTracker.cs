using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Stackroot.App.Commands;
using Stackroot.App.ViewModels;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;

namespace Stackroot.App.Services;

public sealed class InstallProgressTracker
{
    private readonly Dispatcher _dispatcher;
    private readonly PackageInstaller _installer;
    private readonly IDiagnosticsReporter _diagnostics;
    private readonly Dictionary<string, InstallQueueItemViewModel> _items = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _changedTimer;
    private DispatcherTimer? _pruneTimer;

    public InstallProgressTracker(PackageInstaller installer, IDiagnosticsReporter diagnostics)
    {
        _installer = installer;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _diagnostics = diagnostics;
        Items = new ObservableCollection<InstallQueueItemViewModel>();
        installer.ProgressChanged += OnProgressChanged;
        StartPruneTimer();
    }

    public ObservableCollection<InstallQueueItemViewModel> Items { get; }

    public int ActiveCount => Items.Count(item => item.IsActive);

    public event EventHandler? Changed;

    public bool IsInstalling(string packageId) => _installer.IsInstalling(packageId);

    public bool IsInstallingType(PackageType type)
    {
        var prefix = $"{type.ToString().ToLowerInvariant()}-";
        return Items.Any(item => item.IsActive && item.PackageId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public void CancelInstall(string packageId)
    {
        _installer.CancelInstall(packageId);
        _diagnostics.LogActivity("PackageInstall", $"CANCEL requested: {packageId}");
    }

    public void Dismiss(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return;
        }

        if (_items.Remove(packageId, out var removed))
        {
            Items.Remove(removed);
            ScheduleChanged();
        }
    }

    private void OnProgressChanged(object? sender, InstallProgress progress)
    {
        if (_dispatcher.CheckAccess())
        {
            Upsert(progress);
            return;
        }

        _dispatcher.BeginInvoke(() => Upsert(progress));
    }

    private void Upsert(InstallProgress progress)
    {
        if (!_items.TryGetValue(progress.PackageId, out var item))
        {
            var packageId = progress.PackageId;
            item = new InstallQueueItemViewModel
            {
                PackageId = packageId,
                CancelCommand = new RelayCommand(
                    _ => CancelInstall(packageId),
                    _ => _items.TryGetValue(packageId, out var row) && row.IsActive)
            };
            _items[packageId] = item;
            Items.Add(item);
            _diagnostics.LogActivity("PackageInstall", $"START install: {packageId}");
        }

        var previousPhase = item.Phase;
        item.Update(progress);
        item.CancelCommand.RaiseCanExecuteChanged();

        if (progress.Phase == InstallPhase.Error)
        {
            if (progress.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase))
            {
                _diagnostics.LogActivity("PackageInstall", $"CANCELLED install: {progress.PackageId}");
            }
            else
            {
                _diagnostics.LogUserError("PackageInstall", $"{progress.PackageId}: {progress.Message}");
            }
        }
        else if (progress.Phase == InstallPhase.Done)
        {
            _diagnostics.LogActivity("PackageInstall", $"DONE install: {progress.PackageId} (100%)");
        }
        else if (previousPhase != progress.Phase)
        {
            _diagnostics.LogActivity(
                "PackageInstall",
                $"{progress.PackageId} — {progress.Phase} ({progress.Percent}%): {progress.Message}");
        }

        ScheduleChanged();
    }

    private void ScheduleChanged()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke((Action)ScheduleChanged);
            return;
        }

        _changedTimer ??= CreateChangedTimer();
        if (_changedTimer.IsEnabled)
        {
            return;
        }

        _changedTimer.Start();
    }

    private DispatcherTimer CreateChangedTimer()
    {
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Changed?.Invoke(this, EventArgs.Empty);
        };
        return timer;
    }

    private void StartPruneTimer()
    {
        _pruneTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _pruneTimer.Tick += (_, _) => PruneFinished();
        _pruneTimer.Start();
    }

    private void PruneFinished()
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-2);
        var stale = Items
            .Where(item => item.FinishedAt is not null && item.FinishedAt < cutoff)
            .Select(item => item.PackageId)
            .ToList();

        foreach (var packageId in stale)
        {
            if (_items.Remove(packageId, out var removed))
            {
                Items.Remove(removed);
            }
        }

        if (stale.Count > 0)
        {
            ScheduleChanged();
        }
    }
}

public sealed class InstallQueueItemViewModel : ViewModelBase
{
    private string _displayName = string.Empty;
    private string _phaseLabel = string.Empty;
    private string _message = string.Empty;
    private int _percent;
    private InstallPhase _phase;
    private bool _isActive = true;
    private string _phaseColor = "#91A0B5";

    public string PackageId { get; init; } = string.Empty;

    public RelayCommand CancelCommand { get; init; } = new(_ => { });

    public DateTimeOffset? FinishedAt { get; private set; }

    public string DisplayName
    {
        get => _displayName;
        private set => SetProperty(ref _displayName, value);
    }

    public string PhaseLabel
    {
        get => _phaseLabel;
        private set => SetProperty(ref _phaseLabel, value);
    }

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public int Percent
    {
        get => _percent;
        private set => SetProperty(ref _percent, value);
    }

    public InstallPhase Phase
    {
        get => _phase;
        private set => SetProperty(ref _phase, value);
    }

    public bool IsActive
    {
        get => _isActive;
        private set => SetProperty(ref _isActive, value);
    }

    public string PhaseColor
    {
        get => _phaseColor;
        private set => SetProperty(ref _phaseColor, value);
    }

    public bool ShowProgress => IsActive;
    public bool ShowCancel => IsActive;

    public void Update(InstallProgress progress)
    {
        DisplayName = FormatDisplayName(progress.PackageId);
        Phase = progress.Phase;
        Percent = progress.Percent;
        Message = progress.Message;
        PhaseLabel = FormatPhaseLabel(progress.Phase, progress.Message);
        PhaseColor = progress.Phase switch
        {
            InstallPhase.Done => "#8FD6B6",
            InstallPhase.Error => "#EAAAB0",
            _ => "#91A0B5"
        };
        IsActive = progress.Phase is not InstallPhase.Done and not InstallPhase.Error;
        FinishedAt = IsActive ? null : DateTimeOffset.UtcNow;
        RaisePropertyChanged(nameof(ShowProgress));
        RaisePropertyChanged(nameof(ShowCancel));
        CancelCommand.RaiseCanExecuteChanged();
    }

    private static string FormatDisplayName(string packageId)
    {
        if (packageId.StartsWith("node:", StringComparison.OrdinalIgnoreCase))
        {
            return $"Node {packageId["node:".Length..]}";
        }

        return packageId;
    }

    private static string FormatPhaseLabel(InstallPhase phase, string message)
    {
        if (phase == InstallPhase.Registering && message.Contains("remov", StringComparison.OrdinalIgnoreCase))
        {
            return "Removing";
        }

        return phase switch
        {
            InstallPhase.Resolving => "Preparing",
            InstallPhase.Downloading => "Downloading",
            InstallPhase.Extracting => "Extracting",
            InstallPhase.Registering => "Finishing",
            InstallPhase.Done => "Done",
            InstallPhase.Error => "Failed",
            _ => phase.ToString()
        };
    }
}
