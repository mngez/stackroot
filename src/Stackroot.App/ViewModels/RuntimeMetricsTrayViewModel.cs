using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Stackroot.App.Commands;
using Stackroot.App.Services;

namespace Stackroot.App.ViewModels;

public sealed class RuntimeMetricsTrayViewModel : ViewModelBase
{
    private readonly RuntimeMetricsService _metrics;
    private readonly IServiceProvider _services;
    private string _summaryText = "-";

    public RuntimeMetricsTrayViewModel(RuntimeMetricsService metrics, IServiceProvider services)
    {
        _metrics = metrics;
        _services = services;
        OpenPerformanceCommand = new RelayCommand(_ => OpenPerformancePage());
        RefreshCommand = new RelayCommand(_ => _ = RefreshMetricsAsync(), _ => !IsRefreshing);
        _metrics.SnapshotUpdated += OnSnapshotUpdated;
        ApplyFromService();
    }

    public RelayCommand OpenPerformanceCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private bool _isRefreshing;

    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    private void OnSnapshotUpdated(object? sender, EventArgs e)
    {
        // RuntimeMetricsService already marshals at ApplicationIdle — apply directly.
        ApplyFromService();
    }

    private void ApplyFromService()
    {
        SummaryText = _metrics.SummaryText;
    }

    private void OpenPerformancePage()
    {
        _services.GetRequiredService<ShellViewModel>().Navigate("performance");
    }

    private async Task RefreshMetricsAsync()
    {
        IsRefreshing = true;
        try
        {
            await _metrics.RefreshAsync(measureCpu: true).ConfigureAwait(false);
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                ApplyFromService();
            }
            else
            {
                await dispatcher.InvokeAsync(ApplyFromService, DispatcherPriority.Background).Task.ConfigureAwait(false);
            }
        }
        finally
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                IsRefreshing = false;
            }
            else
            {
                await dispatcher.InvokeAsync(() => IsRefreshing = false).Task.ConfigureAwait(false);
            }
        }
    }
}
