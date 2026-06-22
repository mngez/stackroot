using System.Windows.Threading;
using Stackroot.App.Commands;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Supervisor;

namespace Stackroot.App.ViewModels;

public sealed class SiteProcessLogDialogViewModel : ViewModelBase, IDisposable
{
    private readonly GlobalProcessManager _processManager;
    private readonly string _processId;
    private readonly DispatcherTimer _timer;
    private bool _disposed;
    private string _processName = string.Empty;
    private string _commandLine = string.Empty;
    private string _logContent = "(no output yet)";
    private string _statusMessage = string.Empty;
    private bool _isLoading;
    private bool _liveUpdates = true;
    private bool _isRunning;
    private string? _pid;

    public SiteProcessLogDialogViewModel(GlobalProcessManager processManager, string processId, string processName)
    {
        _processManager = processManager;
        _processId = processId;
        ProcessName = processName;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _timer.Tick += async (_, _) => await RefreshAsync();

        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(scroll: false), _ => !IsLoading);
        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty));

        _ = RefreshAsync();
        StartPolling();
    }

    public string Title => $"Log — {ProcessName}";

    public string ProcessName
    {
        get => _processName;
        private set => SetProperty(ref _processName, value);
    }

    public string CommandLine
    {
        get => _commandLine;
        private set => SetProperty(ref _commandLine, value);
    }

    public string LogContent
    {
        get => _logContent;
        private set => SetProperty(ref _logContent, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool LiveUpdates
    {
        get => _liveUpdates;
        set
        {
            if (!SetProperty(ref _liveUpdates, value))
            {
                return;
            }

            RaisePropertyChanged(nameof(ShowRefreshButton));

            if (value)
            {
                StartPolling();
            }
            else
            {
                StopPolling();
            }
        }
    }

    public bool ShowRefreshButton => !LiveUpdates;

    public bool IsRunning
    {
        get => _isRunning;
        private set => SetProperty(ref _isRunning, value);
    }

    public string? Pid
    {
        get => _pid;
        private set => SetProperty(ref _pid, value);
    }

    public string RunningLabel => IsRunning ? "Running" : "Stopped";
    public string? PidLabel => string.IsNullOrWhiteSpace(Pid) ? null : $"pid {Pid}";

    public RelayCommand RefreshCommand { get; }
    public RelayCommand CloseCommand { get; }

    public event EventHandler? RequestClose;

    private void StartPolling()
    {
        if (!LiveUpdates)
        {
            return;
        }

        _timer.Start();
    }

    private void StopPolling()
    {
        _timer.Stop();
    }

    private async Task RefreshAsync(bool scroll = true)
    {
        if (_disposed || IsLoading)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var log = await Task.Run(() => _processManager.GetLog(_processId));
            ApplyLog(log);
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }

        _ = scroll;
    }

    private void ApplyLog(ProcessLog log)
    {
        var commandLine = log.CommandLine ?? string.Empty;
        if (!string.Equals(CommandLine, commandLine, StringComparison.Ordinal))
        {
            CommandLine = commandLine;
        }

        var newContent = string.IsNullOrWhiteSpace(log.Content) ? "(no output yet)" : log.Content;
        if (!string.Equals(_logContent, newContent, StringComparison.Ordinal))
        {
            LogContent = newContent;
        }

        IsRunning = log.Running;
        Pid = log.Pid?.ToString();
        RaisePropertyChanged(nameof(RunningLabel));
        RaisePropertyChanged(nameof(PidLabel));

        if (!log.Running)
        {
            DisableLiveUpdates();
        }
    }

    private void DisableLiveUpdates()
    {
        if (!LiveUpdates)
        {
            return;
        }

        LiveUpdates = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopPolling();
    }
}
