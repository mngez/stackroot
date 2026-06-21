using Stackroot.App.Commands;
using Stackroot.App.Helpers;
using Stackroot.Core.Windows;
using System.Windows.Media;

namespace Stackroot.App.ViewModels;

public sealed class DashboardServiceRowViewModel : ViewModelBase, IUptimeTooltipTarget
{
    private string _statusText = "Stopped";
    private string _statusColor = "#91A0B5";
    private bool _isBusy;
    private bool _isRunning;
    private bool _showStartupProgress;
    private string? _message;
    private string _uptimeText = string.Empty;
    private int? _uptimePid;
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
    private bool _isSupervised;

    public bool IsSupervised
    {
        get => _isSupervised;
        set => SetProperty(ref _isSupervised, value);
    }
    public required RelayCommand StartCommand { get; init; }
    public required RelayCommand StopCommand { get; init; }
    public required RelayCommand RestartCommand { get; init; }
    public RelayCommand? SettingsCommand { get; init; }

    public void MarkStarting()
    {
        NoteStarted();
        IsBusy = true;
        Message = null;
        IsRunning = false;
        StatusText = "Starting";
        StatusColor = "#E9BD5B";
    }

    public void MarkRestarting()
    {
        NoteStarted();
        IsBusy = true;
        IsRunning = false;
        StatusText = "Restarting";
        StatusColor = "#E9BD5B";
    }

    public void MarkRunning()
    {
        NoteStarted();
        IsBusy = false;
        SetStartupProgress(false);
        IsRunning = true;
        StatusText = ServiceKey is "imagemagick" ? "Ready" : "Running";
        StatusColor = "#8FD6B6";
        Message = null;
    }

    public void MarkError(string message)
    {
        NoteStarted();
        IsBusy = false;
        SetStartupProgress(false);
        IsRunning = false;
        Message = message;
        StatusText = "Error";
        StatusColor = "#EAAAB0";
    }

    public void MarkStopped()
    {
        IsBusy = false;
        SetStartupProgress(false);
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

    public bool IsLive => IsRunning;
    public bool ShowStartButton => !IsRunning && !IsBusy;
    public bool ShowStopButton => IsRunning && !IsBusy;
    public bool ShowRestartButton => IsRunning && !IsBusy;

    public bool ShowProgressBar => IsBusy || ShowStartupProgress;

    public bool ShowStartupProgress => _showStartupProgress;

    public void SetStartupProgress(bool active)
    {
        if (_showStartupProgress == active)
        {
            return;
        }

        _showStartupProgress = active;
        RaisePropertyChanged(nameof(ShowStartupProgress));
        RaisePropertyChanged(nameof(ShowProgressBar));
    }

    /// <summary>
    /// True when the user or auto-start has run this service and it is expected to stay up
    /// (unless the user explicitly stopped it). Used for environment health — not for keep-alive rows.
    /// </summary>
    public bool ExpectRunning { get; private set; }

    public void NoteStarted()
    {
        ExpectRunning = true;
        RaisePresentationChanged();
    }

    public void NoteUserStopped()
    {
        ExpectRunning = false;
        RaisePresentationChanged();
    }

    /// <summary>
    /// Service was started (manually or auto-start) but is no longer running — needs attention.
    /// </summary>
    public bool NeedsAttention =>
        !IsRunning && !IsBusy &&
        (StatusText == "Error" || (ExpectRunning && StatusText == "Stopped"));

    public System.Windows.Media.Brush IndicatorColor => CreateBrush(
        IsRunning ? "#4CAE8C" :
        NeedsAttention ? "#E88A92" :
        StatusText is "Starting" or "Restarting" ? "#E9BD5B" :
        "#91A0B5");

    public System.Windows.Media.Brush RowBorderBrush => CreateBrush(
        IsRunning ? "#4D4CAE8C" :
        NeedsAttention ? "#CCE88A92" :
        StatusText is "Starting" or "Restarting" ? "#66E9BD5B" :
        "#353F52");

    public System.Windows.Media.Brush RowBackgroundBrush => CreateBrush("#121A26");

    public void RefreshCommandStates()
    {
        SettingsCommand?.RaiseCanExecuteChanged();
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        RestartCommand.RaiseCanExecuteChanged();
    }

    private void RaisePresentationChanged()
    {
        RefreshAttentionPresentation();
        RaisePropertyChanged(nameof(IsLive));
        RaisePropertyChanged(nameof(ShowStartButton));
        RaisePropertyChanged(nameof(ShowStopButton));
        RaisePropertyChanged(nameof(ShowRestartButton));
        RaisePropertyChanged(nameof(NeedsAttention));
        RaisePropertyChanged(nameof(IndicatorColor));
        RaisePropertyChanged(nameof(RowBorderBrush));
        RaisePropertyChanged(nameof(RowBackgroundBrush));
    }

    private void RefreshAttentionPresentation()
    {
        var color = StatusText == "Error" || NeedsAttention ? "#EAAAB0" :
            IsRunning || StatusText == "Ready" ? "#8FD6B6" :
            StatusText is "Starting" or "Restarting" ? "#E9BD5B" :
            "#91A0B5";
        if (_statusColor != color)
        {
            _statusColor = color;
            RaisePropertyChanged(nameof(StatusColor));
        }
    }

    private static SolidColorBrush CreateBrush(string hex) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);
}
