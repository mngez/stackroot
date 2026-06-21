using System.IO;
using System.Windows.Threading;
using Stackroot.App.Commands;

namespace Stackroot.App.ViewModels;

public sealed class FileLogDialogViewModel : ViewModelBase, IDisposable
{
    private readonly string _logPath;
    private readonly DispatcherTimer _timer;
    private bool _disposed;
    private string _title = "Log";
    private string _logContent = "(no output yet)";
    private string _statusMessage = string.Empty;
    private bool _isLoading;
    private bool _liveUpdates = true;

    public FileLogDialogViewModel(string logPath, string title)
    {
        _logPath = logPath;
        Title = title;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += async (_, _) => await RefreshAsync();

        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(scroll: false), _ => !IsLoading);
        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty));

        _ = RefreshAsync();
        StartPolling();
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
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
            var content = await Task.Run(ReadTail);
            LogContent = string.IsNullOrWhiteSpace(content) ? "(no output yet)" : content;
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
        using var reader = new StreamReader(stream);
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
