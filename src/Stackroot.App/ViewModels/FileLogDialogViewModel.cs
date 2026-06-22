using System.IO;
using System.Text;
using System.Windows.Threading;
using Stackroot.App.Commands;
using Stackroot.App.Helpers;

namespace Stackroot.App.ViewModels;

public sealed class FileLogDialogViewModel : ViewModelBase, IDisposable
{
    private readonly string _logPath;
    private readonly DispatcherTimer _timer;
    private readonly Func<Task>? _cancelAsync;
    private readonly Func<bool>? _isRunning;
    private readonly Func<(int ExitCode, long DurationMs)?>? _getCompletion;
    private bool _disposed;
    private string _title = "Log";
    private string _commandLine = string.Empty;
    private string _logFooterLine = string.Empty;
    private string _logContent = "(no output yet)";
    private string _statusMessage = string.Empty;
    private bool _isLoading;
    private bool _liveUpdates = true;
    private bool _canCancel;

    public FileLogDialogViewModel(
        string logPath,
        string title,
        Func<Task>? cancelAsync = null,
        Func<bool>? isRunning = null,
        SiteLogChrome? chrome = null)
    {
        _logPath = logPath;
        _cancelAsync = cancelAsync;
        _isRunning = isRunning;
        _getCompletion = chrome?.GetCompletion;
        Title = title;
        CommandLine = chrome?.CommandLine ?? string.Empty;
        _canCancel = isRunning?.Invoke() == true;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += async (_, _) => await RefreshAsync();

        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(scroll: false), _ => !IsLoading);
        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty));
        if (_cancelAsync is not null)
        {
            CancelCommand = new RelayCommand(_ => _ = CancelAsync(), _ => CanCancel);
        }

        if (isRunning?.Invoke() == false)
        {
            LiveUpdates = false;
        }

        _ = RefreshAsync();
        if (LiveUpdates)
        {
            StartPolling();
        }
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public string CommandLine
    {
        get => _commandLine;
        private set
        {
            if (SetProperty(ref _commandLine, value))
            {
                RaisePropertyChanged(nameof(LogHeaderLine));
                RaisePropertyChanged(nameof(ShowLogHeader));
            }
        }
    }

    public string LogHeaderLine =>
        string.IsNullOrWhiteSpace(CommandLine) ? string.Empty : $"# {CommandLine}";

    public bool ShowLogHeader => !string.IsNullOrWhiteSpace(LogHeaderLine);

    public string LogFooterLine
    {
        get => _logFooterLine;
        private set
        {
            if (SetProperty(ref _logFooterLine, value))
            {
                RaisePropertyChanged(nameof(ShowLogFooter));
            }
        }
    }

    public bool ShowLogFooter => !string.IsNullOrWhiteSpace(LogFooterLine);

    public string ProcessName => string.Empty;

    public string RunningLabel => _isRunning?.Invoke() == true ? "Running" : "Stopped";

    public string? PidLabel => null;

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

    public bool CanCancel
    {
        get => _canCancel;
        private set
        {
            if (SetProperty(ref _canCancel, value))
            {
                CancelCommand?.RaiseCanExecuteChanged();
                RaisePropertyChanged(nameof(ShowCancelButton));
            }
        }
    }

    public bool ShowCancelButton => CancelCommand is not null;

    public RelayCommand RefreshCommand { get; }
    public RelayCommand CloseCommand { get; }
    public RelayCommand? CancelCommand { get; }

    public event EventHandler? RequestClose;

    private async Task CancelAsync()
    {
        if (_cancelAsync is null || !CanCancel)
        {
            return;
        }

        CanCancel = false;
        try
        {
            await _cancelAsync().ConfigureAwait(true);
            StatusMessage = "Cancelling command…";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

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
            var content = await Task.Run(ReadTail);
            LogContent = string.IsNullOrWhiteSpace(content) ? "(no output yet)" : content;
            StatusMessage = string.Empty;
            if (_isRunning is not null)
            {
                var running = _isRunning();
                CanCancel = running;
                RaisePropertyChanged(nameof(RunningLabel));
                if (!running)
                {
                    DisableLiveUpdates();
                }
            }

            UpdateLogFooter();
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

    private string ReadTail()
    {
        if (!File.Exists(_logPath))
        {
            return "(log file not found)";
        }

        using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length == 0)
        {
            return string.Empty;
        }

        const int maxBytes = 512 * 1024;
        var start = stream.Length > maxBytes ? stream.Length - maxBytes : 0;
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var content = reader.ReadToEnd();
        if (start > 0)
        {
            var firstBreak = content.IndexOf('\n');
            if (firstBreak >= 0)
            {
                content = content[(firstBreak + 1)..];
            }
        }

        return content;
    }

    private void UpdateLogFooter()
    {
        if (_isRunning?.Invoke() == true)
        {
            LogFooterLine = "# running…";
            return;
        }

        var completion = _getCompletion?.Invoke();
        LogFooterLine = completion is { } result
            ? $"# exit {result.ExitCode} · {result.DurationMs}ms"
            : string.Empty;
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
