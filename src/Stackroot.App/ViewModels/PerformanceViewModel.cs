using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Stackroot.App.Commands;
using Stackroot.App.Services;
using Stackroot.Core.Observability;

namespace Stackroot.App.ViewModels;

public sealed class PerformanceViewModel : ViewModelBase, IDisposable
{
    private readonly RuntimeMetricsService _metrics;
    private readonly RuntimeStateService _runtimeState;
    private string _sampledAt = "-";
    private string _serviceTotalMemory = "0 MB";
    private string _phpTotalMemory = "0 MB";
    private string _processTotalMemory = "0 MB";
    private bool _hasPhpListeners;
    private bool _hasProcesses;
    private bool _disposed;

    public PerformanceViewModel(RuntimeMetricsService metrics, RuntimeStateService runtimeState)
    {
        _metrics = metrics;
        _runtimeState = runtimeState;
        ServiceItems = [];
        PhpItems = [];
        ProcessItems = [];
        RefreshCommand = new RelayCommand(_ => _ = _metrics.RefreshAsync(measureCpu: true));
        _metrics.SnapshotUpdated += OnMetricsSnapshotUpdated;
    }

    public ObservableCollection<PerformanceRowViewModel> ServiceItems { get; }
    public ObservableCollection<PerformanceRowViewModel> PhpItems { get; }
    public ObservableCollection<PerformanceRowViewModel> ProcessItems { get; }
    public RelayCommand RefreshCommand { get; }

    public string SampledAt
    {
        get => _sampledAt;
        private set => SetProperty(ref _sampledAt, value);
    }

    public string ServiceTotalMemory
    {
        get => _serviceTotalMemory;
        private set => SetProperty(ref _serviceTotalMemory, value);
    }

    public string PhpTotalMemory
    {
        get => _phpTotalMemory;
        private set => SetProperty(ref _phpTotalMemory, value);
    }

    public string ProcessTotalMemory
    {
        get => _processTotalMemory;
        private set => SetProperty(ref _processTotalMemory, value);
    }

    public bool HasPhpListeners
    {
        get => _hasPhpListeners;
        private set => SetProperty(ref _hasPhpListeners, value);
    }

    public bool HasProcesses
    {
        get => _hasProcesses;
        private set => SetProperty(ref _hasProcesses, value);
    }

    public void BeginLoading()
    {
        _metrics.SetDetailedPolling(enabled: true);
        _runtimeState.SetDetailedPolling(enabled: true);
        ApplyFromService();
        _ = _metrics.RefreshAsync(measureCpu: true);
    }

    public void EndLoading()
    {
        _metrics.SetDetailedPolling(enabled: false);
        _runtimeState.SetDetailedPolling(enabled: false);
    }

    public void Refresh() => _ = _metrics.RefreshAsync(measureCpu: true);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _metrics.SnapshotUpdated -= OnMetricsSnapshotUpdated;
    }

    private void OnMetricsSnapshotUpdated(object? sender, EventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyFromService();
            return;
        }

        dispatcher.BeginInvoke(ApplyFromService, DispatcherPriority.Background);
    }

    private void ApplyFromService()
    {
        var snapshot = _metrics.LatestSnapshot;
        if (snapshot is null)
        {
            return;
        }

        var serviceRows = snapshot.Items
            .Where(item => item.Kind is PerformanceItemKind.App or PerformanceItemKind.Service)
            .OrderByDescending(row => row.MemoryMb ?? 0)
            .Select(ToRow)
            .ToList();

        var phpRows = snapshot.Items
            .Where(item => item.Kind == PerformanceItemKind.PhpListener)
            .OrderByDescending(row => row.MemoryMb ?? 0)
            .Select(ToRow)
            .ToList();

        var processRows = snapshot.Items
            .Where(item => item.Kind == PerformanceItemKind.Process)
            .OrderByDescending(row => row.MemoryMb ?? 0)
            .Select(ToRow)
            .ToList();

        SyncRows(ServiceItems, serviceRows);
        SyncRows(PhpItems, phpRows);
        SyncRows(ProcessItems, processRows);

        SampledAt = snapshot.SampledAt.ToLocalTime().ToString("g");
        ServiceTotalMemory = FormatTotalMb(serviceRows.Sum(row => row.MemoryMb ?? 0));
        PhpTotalMemory = FormatTotalMb(phpRows.Sum(row => row.MemoryMb ?? 0));
        ProcessTotalMemory = FormatTotalMb(processRows.Sum(row => row.MemoryMb ?? 0));
        HasPhpListeners = phpRows.Count > 0;
        HasProcesses = processRows.Count > 0;
    }

    private static void SyncRows(
        ObservableCollection<PerformanceRowViewModel> target,
        IReadOnlyList<PerformanceRowViewModel> incoming)
    {
        var incomingIds = incoming.Select(row => row.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!incomingIds.Contains(target[i].Id))
            {
                target.RemoveAt(i);
            }
        }

        for (var index = 0; index < incoming.Count; index++)
        {
            var row = incoming[index];
            var existingIndex = -1;
            for (var i = 0; i < target.Count; i++)
            {
                if (string.Equals(target[i].Id, row.Id, StringComparison.OrdinalIgnoreCase))
                {
                    existingIndex = i;
                    break;
                }
            }

            if (existingIndex >= 0)
            {
                var existing = target[existingIndex];
                existing.ApplyFrom(row);
                if (existingIndex != index)
                {
                    target.RemoveAt(existingIndex);
                    target.Insert(index, existing);
                }
            }
            else
            {
                target.Insert(index, row);
            }
        }
    }

    private static PerformanceRowViewModel ToRow(ProcessPerformance item)
    {
        var row = new PerformanceRowViewModel { Id = item.Id };
        row.ApplyFrom(item);
        return row;
    }

    private static string FormatTotalMb(double totalMb) => $"{totalMb:F1} MB";
}

public sealed class PerformanceRowViewModel : ViewModelBase
{
    private string _label = string.Empty;
    private string _kind = string.Empty;
    private string _status = "-";
    private string _endpoint = string.Empty;
    private string _pid = "-";
    private double? _memoryMb;
    private double? _cpuPercent;

    public string Id { get; init; } = string.Empty;

    public string Label
    {
        get => _label;
        private set => SetProperty(ref _label, value);
    }

    public string Kind
    {
        get => _kind;
        private set => SetProperty(ref _kind, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string Endpoint
    {
        get => _endpoint;
        private set => SetProperty(ref _endpoint, value);
    }

    public string Pid
    {
        get => _pid;
        private set => SetProperty(ref _pid, value);
    }

    public double? MemoryMb
    {
        get => _memoryMb;
        private set
        {
            if (SetProperty(ref _memoryMb, value))
            {
                RaisePropertyChanged(nameof(Memory));
            }
        }
    }

    public double? CpuPercent
    {
        get => _cpuPercent;
        private set
        {
            if (SetProperty(ref _cpuPercent, value))
            {
                RaisePropertyChanged(nameof(Cpu));
            }
        }
    }

    public string Memory => MemoryMb.HasValue ? $"{MemoryMb.Value:F1} MB" : "-";
    public string Cpu => CpuPercent.HasValue ? $"{CpuPercent.Value:F1}%" : "-";

    public void ApplyFrom(ProcessPerformance item)
    {
        Label = item.Label;
        Kind = item.Kind.ToString();
        Status = item.Status ?? "-";
        Endpoint = item.Endpoint ?? string.Empty;
        Pid = item.Pid?.ToString() ?? "-";
        MemoryMb = item.MemoryMb;
        CpuPercent = item.CpuPercent;
    }

    public void ApplyFrom(PerformanceRowViewModel source)
    {
        Label = source.Label;
        Kind = source.Kind;
        Status = source.Status;
        Endpoint = source.Endpoint;
        Pid = source.Pid;
        MemoryMb = source.MemoryMb;
        CpuPercent = source.CpuPercent;
    }
}
