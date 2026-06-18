using System.Collections.ObjectModel;
using System.Windows.Threading;
using Stackroot.App.Commands;
using Stackroot.Core.Observability;
using Stackroot.Core.Services;
using Stackroot.Core.Supervisor;

namespace Stackroot.App.ViewModels;

public sealed class PerformanceViewModel : ViewModelBase
{
    private readonly ServiceManager _serviceManager;
    private readonly GlobalProcessManager _globalProcessManager;
    private readonly ProcessSupervisor _processSupervisor;
    private readonly PerformanceSampler _performanceSampler;
    private DispatcherTimer? _pollTimer;
    private string _sampledAt = "-";
    private string _totalMemory = "0 MB";
    private int _refreshInFlight;

    public PerformanceViewModel(
        ServiceManager serviceManager,
        GlobalProcessManager globalProcessManager,
        ProcessSupervisor processSupervisor,
        PerformanceSampler performanceSampler)
    {
        _serviceManager = serviceManager;
        _globalProcessManager = globalProcessManager;
        _processSupervisor = processSupervisor;
        _performanceSampler = performanceSampler;

        Items = [];
        RefreshCommand = new RelayCommand(_ => Refresh());
    }

    public ObservableCollection<PerformanceRowViewModel> Items { get; }
    public RelayCommand RefreshCommand { get; }

    public string SampledAt
    {
        get => _sampledAt;
        private set => SetProperty(ref _sampledAt, value);
    }

    public string TotalMemory
    {
        get => _totalMemory;
        private set => SetProperty(ref _totalMemory, value);
    }

    public void BeginLoading()
    {
        Refresh();
        StartAutoPoll();
    }

    public void EndLoading()
    {
        StopAutoPoll();
    }

    public void Refresh() => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        if (Interlocked.CompareExchange(ref _refreshInFlight, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var snapshot = await Task.Run(() =>
            {
                var services = _serviceManager.ListManagedSnapshot()
                    .Select(service => new ServicePerformanceTarget
                    {
                        Id = service.Id,
                        Name = service.Name,
                        Status = service.Status.ToString(),
                        Pid = service.Pid
                    })
                    .ToList();

                var processMap = _globalProcessManager.List()
                    .ToDictionary(
                        process => process.Id,
                        process => new ProcessPerformanceTarget
                        {
                            Id = process.Id,
                            Name = process.Name,
                            Status = process.Status.ToString(),
                            Pid = process.Pid,
                            SiteName = process.SiteId
                        },
                        StringComparer.OrdinalIgnoreCase);

                foreach (var status in _processSupervisor.ListStatuses())
                {
                    processMap[status.Scope.ProcessId] = new ProcessPerformanceTarget
                    {
                        Id = status.Scope.ProcessId,
                        Name = status.Label,
                        Status = status.Status.ToString(),
                        Pid = status.Pid,
                        SiteName = status.Scope.SiteId
                    };
                }

                return _performanceSampler.SamplePerformance(
                    services,
                    processMap.Values.ToList(),
                    Environment.ProcessId);
            });

            Items.Clear();
            foreach (var item in snapshot.Items.OrderByDescending(row => row.MemoryMb ?? 0))
            {
                Items.Add(new PerformanceRowViewModel
                {
                    Id = item.Id,
                    Label = item.Label,
                    Kind = item.Kind.ToString(),
                    Status = item.Status ?? "-",
                    Pid = item.Pid?.ToString() ?? "-",
                    Memory = item.MemoryMb.HasValue ? $"{item.MemoryMb.Value:F1} MB" : "-"
                });
            }

            SampledAt = snapshot.SampledAt.ToLocalTime().ToString("g");
            var total = snapshot.Items.Sum(item => item.MemoryMb ?? 0);
            TotalMemory = $"{total:F1} MB";
        }
        finally
        {
            Interlocked.Exchange(ref _refreshInFlight, 0);
        }
    }

    private void StartAutoPoll()
    {
        _pollTimer ??= new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10)
        };

        _pollTimer.Tick -= OnPollTimerTick;
        _pollTimer.Tick += OnPollTimerTick;

        if (!_pollTimer.IsEnabled)
        {
            _pollTimer.Start();
        }
    }

    private void StopAutoPoll()
    {
        if (_pollTimer is null)
        {
            return;
        }

        _pollTimer.Stop();
        _pollTimer.Tick -= OnPollTimerTick;
    }

    private void OnPollTimerTick(object? sender, EventArgs e) => Refresh();
}

public sealed class PerformanceRowViewModel
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Pid { get; init; } = "-";
    public string Memory { get; init; } = "-";
}
