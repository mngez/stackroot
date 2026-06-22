using System.IO;
using System.Text;
using System.Windows.Threading;
using Stackroot.App.Commands;
using Stackroot.App.Helpers;

namespace Stackroot.App.ViewModels;

public sealed class FileLogDialogViewModel : ViewModelBase, IDisposable
{
    private static readonly string[] PlaceholderContents =
    [
        "(no output yet)",
        "(starting…)",
        "(log file not found)"
    ];

    private readonly SiteLogSession _session;
    private readonly DispatcherTimer _timer;
    private bool _disposed;
    private string _title = "Log";
    private string _commandLine = string.Empty;
    private string _logFooterLine = string.Empty;
    private string _logContent = "(no output yet)";
    private string _statusMessage = string.Empty;
    private bool _isLoading;
    private bool _liveUpdates = true;
    private bool _canCancel;
    private bool _isCancelling;
    private bool _isRunningAgain;

    public FileLogDialogViewModel(SiteLogSession session, string title)
    {
        _session = session;
        Title = title;
        CommandLine = session.CommandLine ?? string.Empty;
        _canCancel = session.IsRunning?.Invoke() == true;

        _session.LogPathChanged += OnLogPathChanged;
        _session.Updated += OnSessionUpdated;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += async (_, _) => await RefreshAsync();

        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(scroll: false), _ => !IsLoading);
        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty));
        if (session.RunAgainAsync is not null)
        {
            RunAgainCommand = new RelayCommand(_ => _ = RunAgainAsync(), _ => ShowRunAgainButton);
        }

        if (session.CancelAsync is not null)
        {
            CancelCommand = new RelayCommand(_ => _ = CancelAsync(), _ => CanCancelCommand);
        }

        if (session.IsRunning?.Invoke() == false)
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

    public string RunningLabel => _session.IsRunning?.Invoke() == true ? "Running" : "Stopped";

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
                RunAgainCommand?.RaiseCanExecuteChanged();
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

    public bool ShowRunAgainButton =>
        _session.RunAgainAsync is not null
        && !_isRunningAgain
        && _session.IsRunning?.Invoke() != true;

    public bool CanCancel
    {
        get => _canCancel;
        private set
        {
            if (SetProperty(ref _canCancel, value))
            {
                RaisePropertyChanged(nameof(ShowCancelButton));
                RaisePropertyChanged(nameof(CanCancelCommand));
                CancelCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsCancelling
    {
        get => _isCancelling;
        private set
        {
            if (SetProperty(ref _isCancelling, value))
            {
                RaisePropertyChanged(nameof(ShowCancelButton));
                RaisePropertyChanged(nameof(CancelButtonLabel));
                RaisePropertyChanged(nameof(CanCancelCommand));
                CancelCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public string CancelButtonLabel => IsCancelling ? "Cancelling…" : "Cancel";

    public bool CanCancelCommand => CanCancel && !IsCancelling;

    public bool ShowCancelButton =>
        CancelCommand is not null
        && (CanCancel || IsCancelling);

    public RelayCommand RefreshCommand { get; }
    public RelayCommand CloseCommand { get; }
    public RelayCommand? CancelCommand { get; }
    public RelayCommand? RunAgainCommand { get; }

    public event EventHandler? RequestClose;

    private void OnLogPathChanged(object? sender, EventArgs e)
    {
        CommandLine = _session.CommandLine ?? CommandLine;
        LogContent = "(no output yet)";
        LogFooterLine = string.Empty;
        LiveUpdates = true;
        RaisePropertyChanged(nameof(RunningLabel));
        RaisePropertyChanged(nameof(ShowRunAgainButton));
        RunAgainCommand?.RaiseCanExecuteChanged();
        _ = RefreshAsync();
    }

    private void OnSessionUpdated(object? sender, EventArgs e) => _ = RefreshAsync();

    private async Task RunAgainAsync()
    {
        if (_session.RunAgainAsync is null || _isRunningAgain)
        {
            return;
        }

        _isRunningAgain = true;
        RaisePropertyChanged(nameof(ShowRunAgainButton));
        RunAgainCommand?.RaiseCanExecuteChanged();
        LogContent = "(starting…)";
        LogFooterLine = string.Empty;
        StatusMessage = string.Empty;
        LiveUpdates = true;

        try
        {
            await _session.RunAgainAsync().ConfigureAwait(true);
            await ReadLogWithSettleAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            _isRunningAgain = false;
            RaisePropertyChanged(nameof(ShowRunAgainButton));
            RaisePropertyChanged(nameof(RunningLabel));
            RunAgainCommand?.RaiseCanExecuteChanged();
        }
    }

    private async Task CancelAsync()
    {
        if (_session.CancelAsync is null || !CanCancelCommand)
        {
            return;
        }

        IsCancelling = true;
        StatusMessage = string.Empty;
        LiveUpdates = true;
        _timer.Interval = TimeSpan.FromMilliseconds(200);

        try
        {
            await _session.CancelAsync().ConfigureAwait(true);
            _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            IsCancelling = false;
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
            var running = _session.IsRunning?.Invoke() == true;
            var content = await Task.Run(ReadTail);
            ApplyLogContent(content);
            StatusMessage = string.Empty;

            if (_session.IsRunning is not null)
            {
                running = _session.IsRunning();
                CanCancel = running;
                RaisePropertyChanged(nameof(RunningLabel));
                RaisePropertyChanged(nameof(ShowRunAgainButton));
                RunAgainCommand?.RaiseCanExecuteChanged();

                if (!running)
                {
                    if (IsCancelling)
                    {
                        IsCancelling = false;
                        _timer.Interval = TimeSpan.FromSeconds(1);
                    }

                    if (IsPlaceholder(LogContent))
                    {
                        await ReadLogWithSettleAsync().ConfigureAwait(true);
                    }

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

    private async Task ReadLogWithSettleAsync()
    {
        foreach (var delayMs in new[] { 0, 75, 200 })
        {
            if (delayMs > 0)
            {
                await Task.Delay(delayMs).ConfigureAwait(true);
            }

            var content = await Task.Run(ReadTail).ConfigureAwait(true);
            ApplyLogContent(content);
            UpdateLogFooter();

            if (!IsPlaceholder(LogContent))
            {
                break;
            }
        }
    }

    private void ApplyLogContent(string content)
    {
        LogContent = string.IsNullOrWhiteSpace(content) ? "(no output yet)" : content;
    }

    private static bool IsPlaceholder(string? content) =>
        !string.IsNullOrWhiteSpace(content)
        && PlaceholderContents.Contains(content, StringComparer.Ordinal);

    private string ReadTail()
    {
        var logPath = _session.LogPath;
        if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
        {
            return "(log file not found)";
        }

        using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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
        if (_session.IsRunning?.Invoke() == true)
        {
            LogFooterLine = "# running…";
            return;
        }

        var completion = _session.GetCompletion?.Invoke();
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
        _session.LogPathChanged -= OnLogPathChanged;
        _session.Updated -= OnSessionUpdated;
        StopPolling();
    }
}
