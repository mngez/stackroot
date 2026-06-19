using Stackroot.App.Commands;
using System.Windows.Media;

namespace Stackroot.App.ViewModels;

public sealed class DashboardServiceRowViewModel : ViewModelBase
{
    private string _statusText = "Stopped";
    private string _statusColor = "#91A0B5";
    private bool _isBusy;
    private bool _isRunning;
    private string? _message;
    private int _updateVersion;

    /// <summary>
    /// Monotonically increasing version used to prevent stale
    /// full-refresh data from overwriting newer live-event data.
    /// </summary>
    public int UpdateVersion => _updateVersion;

    public void BumpVersion()
    {
        System.Threading.Interlocked.Increment(ref _updateVersion);
    }

    public required string ServiceKey { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string PortLabel { get; init; }
    public required bool IsSupervised { get; init; }
    public required RelayCommand StartCommand { get; init; }
    public required RelayCommand StopCommand { get; init; }
    public required RelayCommand RestartCommand { get; init; }
    public RelayCommand? SettingsCommand { get; init; }

    public void MarkStarting()
    {
        IsBusy = true;
        Message = null;
        IsRunning = false;
        StatusText = "Starting";
        StatusColor = "#E9BD5B";
    }

    public void MarkRestarting()
    {
        IsBusy = true;
        IsRunning = false;
        StatusText = "Restarting";
        StatusColor = "#E9BD5B";
    }

    public void MarkRunning()
    {
        IsBusy = false;
        IsRunning = true;
        StatusText = ServiceKey is "imagemagick" ? "Ready" : "Running";
        StatusColor = "#8FD6B6";
        Message = null;
    }

    public void MarkError(string message)
    {
        IsBusy = false;
        IsRunning = false;
        Message = message;
        StatusText = "Error";
        StatusColor = "#EAAAB0";
    }

    public void MarkStopped()
    {
        IsBusy = false;
        IsRunning = false;
        StatusText = "Stopped";
        StatusColor = "#91A0B5";
        Message = null;
    }

    public string StatusText
    {
        get => _statusText;
        set
        {
            if (SetProperty(ref _statusText, value))
            {
                RaisePresentationChanged();
            }
        }
    }

    public string StatusColor
    {
        get => _statusColor;
        set => SetProperty(ref _statusColor, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommandStates();
                RaisePresentationChanged();
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (SetProperty(ref _isRunning, value))
            {
                RefreshCommandStates();
                RaisePresentationChanged();
            }
        }
    }

    public string? Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public bool IsLive => IsRunning;
    public bool ShowStartButton => !IsRunning && !IsBusy;
    public bool ShowStopButton => IsRunning && !IsBusy;
    public bool ShowRestartButton => IsRunning && !IsBusy;

    public System.Windows.Media.Brush IndicatorColor => CreateBrush(
        IsRunning ? "#4CAE8C" :
        StatusText == "Error" ? "#E88A92" :
        StatusText is "Starting" or "Restarting" ? "#E9BD5B" :
        "#91A0B5");

    public System.Windows.Media.Brush RowBorderBrush => CreateBrush(
        IsRunning ? "#4D4CAE8C" :
        StatusText == "Error" ? "#59E88A92" :
        StatusText is "Starting" or "Restarting" ? "#66E9BD5B" :
        "#353F52");

    public System.Windows.Media.Brush RowBackgroundBrush => CreateBrush(
        IsRunning ? "#142019" : "#121A26");

    public void RefreshCommandStates()
    {
        SettingsCommand?.RaiseCanExecuteChanged();
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        RestartCommand.RaiseCanExecuteChanged();
    }

    private void RaisePresentationChanged()
    {
        RaisePropertyChanged(nameof(IsLive));
        RaisePropertyChanged(nameof(ShowStartButton));
        RaisePropertyChanged(nameof(ShowStopButton));
        RaisePropertyChanged(nameof(ShowRestartButton));
        RaisePropertyChanged(nameof(IndicatorColor));
        RaisePropertyChanged(nameof(RowBorderBrush));
        RaisePropertyChanged(nameof(RowBackgroundBrush));
    }

    private static SolidColorBrush CreateBrush(string hex) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);
}
