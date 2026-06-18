using Stackroot.App.Commands;
using System.Windows.Media;

namespace Stackroot.App.ViewModels;

public sealed class DashboardProcessRowViewModel : ViewModelBase
{
    private string _statusText = "Stopped";
    private string _statusColor = "#91A0B5";
    private bool _isRunning;
    private bool _isBusy;

    public required string Id { get; init; }
    public required string Name { get; init; }
    public string SiteLabel { get; init; } = string.Empty;
    public string RuntimeLabel { get; init; } = string.Empty;
    public string CommandLine { get; init; } = string.Empty;
    public string WorkDir { get; init; } = string.Empty;
    public bool IsFeatured { get; init; }
    public bool ShowLogButton { get; init; }

    public required RelayCommand StartCommand { get; init; }
    public required RelayCommand StopCommand { get; init; }
    public required RelayCommand RestartCommand { get; init; }
    public RelayCommand? OpenLogCommand { get; init; }

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (SetProperty(ref _isRunning, value))
            {
                RaisePresentationChanged();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaisePresentationChanged();
                StartCommand.RaiseCanExecuteChanged();
                StopCommand.RaiseCanExecuteChanged();
                RestartCommand.RaiseCanExecuteChanged();
            }
        }
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

    public string PinGlyph => IsFeatured ? "\uE735" : "\uE734";
    public System.Windows.Media.Brush PinColor => IsFeatured
        ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE9, 0xBD, 0x5B))
        : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x91, 0xA0, 0xB5));
    public bool IsLive => IsRunning;
    public bool ShowStartButton => !IsRunning && !IsBusy;
    public bool ShowStopButton => IsRunning && !IsBusy;
    public bool ShowRestartButton => IsRunning && !IsBusy;
    public bool ShowBusyIndicator => IsBusy;

    public System.Windows.Media.Brush IndicatorColor => CreateBrush(
        IsRunning ? "#4CAE8C" :
        StatusText == "Error" ? "#E88A92" :
        IsBusy || StatusText == "Restarting" ? "#E9BD5B" :
        "#91A0B5");

    public System.Windows.Media.Brush RowBorderBrush => CreateBrush(
        IsRunning ? "#4D4CAE8C" :
        StatusText == "Error" ? "#59E88A92" :
        IsBusy || StatusText == "Restarting" ? "#66E9BD5B" :
        "#263348");

    private void RaisePresentationChanged()
    {
        RaisePropertyChanged(nameof(IsLive));
        RaisePropertyChanged(nameof(ShowStartButton));
        RaisePropertyChanged(nameof(ShowStopButton));
        RaisePropertyChanged(nameof(ShowRestartButton));
        RaisePropertyChanged(nameof(ShowBusyIndicator));
        RaisePropertyChanged(nameof(IndicatorColor));
        RaisePropertyChanged(nameof(RowBorderBrush));
    }

    private static SolidColorBrush CreateBrush(string hex) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);
}

public sealed class DashboardPhpListenerViewModel : ViewModelBase
{
    private bool _isRunning;
    private string _statusText = "Stopped";
    private string _statusColor = "#91A0B5";
    private string _endpoint = string.Empty;
    private bool _showLogButton;
    private bool _isRestarting;

    public required string VersionId { get; init; }
    public bool IsRequired { get; init; }
    public RelayCommand RestartCommand { get; set; } = null!;
    public required RelayCommand StopCommand { get; init; }
    public RelayCommand? OpenLogCommand { get; init; }

    public bool IsRestarting
    {
        get => _isRestarting;
        set
        {
            if (SetProperty(ref _isRestarting, value))
            {
                RestartCommand.RaiseCanExecuteChanged();
                RaisePropertyChanged(nameof(ShowRestartButton));
            }
        }
    }

    public string Endpoint
    {
        get => _endpoint;
        set => SetProperty(ref _endpoint, value);
    }

    public bool ShowLogButton
    {
        get => _showLogButton;
        set => SetProperty(ref _showLogButton, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (SetProperty(ref _isRunning, value))
            {
                RaisePresentationChanged();
            }
        }
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

    public bool IsLive => IsRunning;
    public bool ShowStopButton => IsRunning;
    public bool ShowRestartButton => IsRequired && !IsRestarting;

    public System.Windows.Media.Brush IndicatorColor => CreateBrush(
        IsRunning ? "#4CAE8C" :
        StatusText == "Error" ? "#E88A92" :
        "#91A0B5");

    private void RaisePresentationChanged()
    {
        RaisePropertyChanged(nameof(IsLive));
        RaisePropertyChanged(nameof(ShowStopButton));
        RaisePropertyChanged(nameof(ShowRestartButton));
        RaisePropertyChanged(nameof(IndicatorColor));
        StopCommand?.RaiseCanExecuteChanged();
        RestartCommand?.RaiseCanExecuteChanged();
    }

    private static SolidColorBrush CreateBrush(string hex) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);
}
