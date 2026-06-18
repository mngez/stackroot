using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Stackroot.App.Commands;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Observability;
using Stackroot.Core.Settings;

namespace Stackroot.App.ViewModels;

public sealed class LogsViewModel : ViewModelBase
{
    private readonly StackrootPaths _paths;
    private readonly LogInventoryService _logInventoryService;
    private readonly SettingsStore _settingsStore;
    private string _statusMessage = string.Empty;
    private string _totalText = "0 B";
    private bool _isLoading;

    public LogsViewModel(
        StackrootPaths paths,
        LogInventoryService logInventoryService,
        SettingsStore settingsStore)
    {
        _paths = paths;
        _logInventoryService = logInventoryService;
        _settingsStore = settingsStore;

        Files = [];
        RefreshCommand = new RelayCommand(_ => Refresh(), _ => !IsLoading);
        CleanupRetentionCommand = new RelayCommand(_ => _ = CleanupRetentionAsync());
        CleanupAllCommand = new RelayCommand(_ => _ = CleanupAllAsync());
        OpenFileCommand = new RelayCommand(file => OpenFile(file as LogFileRowViewModel));

        Refresh();
    }

    public ObservableCollection<LogFileRowViewModel> Files { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand CleanupRetentionCommand { get; }
    public RelayCommand CleanupAllCommand { get; }
    public RelayCommand OpenFileCommand { get; }

    public string TotalText
    {
        get => _totalText;
        private set => SetProperty(ref _totalText, value);
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
                RaisePropertyChanged(nameof(ShowLoadingState));
                RaisePropertyChanged(nameof(ShowDataGrid));
            }
        }
    }

    public bool ShowLoadingState => IsLoading;
    public bool ShowDataGrid => !IsLoading && Files.Count > 0;
    public bool ShowEmptyState => !IsLoading && Files.Count == 0;

    public void Refresh() => _ = RefreshAsync();

    private async Task RefreshAsync(string? statusMessage = null)
    {
        IsLoading = true;
        try
        {
            var inventory = await Task.Run(() => _logInventoryService.ScanLogInventory(_paths));
            Files.Clear();
            foreach (var file in inventory.Files)
            {
                Files.Add(new LogFileRowViewModel
                {
                    Path = file.Path,
                    RelativePath = file.RelativePath,
                    Category = file.Category.ToString(),
                    SizeText = FormatBytes(file.SizeBytes),
                    ModifiedAt = file.ModifiedAt.ToLocalTime().ToString("g")
                });
            }

            TotalText = $"{Files.Count} files - {FormatBytes(inventory.TotalBytes)}";
            StatusMessage = statusMessage ?? string.Empty;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task CleanupRetentionAsync()
    {
        var result = await Task.Run(() =>
        {
            var retentionDays = _settingsStore.Load().General.LogRetentionDays;
            return _logInventoryService.ApplyLogRetention(_paths, retentionDays);
        });
        await RefreshAsync($"Retention cleanup: deleted {result.Deleted} files, freed {FormatBytes(result.FreedBytes)}.");
    }

    private async Task CleanupAllAsync()
    {
        var result = await Task.Run(() => _logInventoryService.CleanupLogs(new CleanupLogsOptions
        {
            LogsRoot = _paths.LogsRoot,
            DeleteAll = true
        }));

        await RefreshAsync($"Deleted {result.Deleted} files and freed {FormatBytes(result.FreedBytes)}.");
    }

    private void OpenFile(LogFileRowViewModel? file)
    {
        if (file is null || string.IsNullOrWhiteSpace(file.Path) || !System.IO.File.Exists(file.Path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = file.Path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:F1} KB";
        if (bytes < 1024L * 1024L * 1024L) return $"{bytes / (1024d * 1024d):F1} MB";
        return $"{bytes / (1024d * 1024d * 1024d):F2} GB";
    }
}

public sealed class LogFileRowViewModel
{
    public string Path { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string SizeText { get; init; } = "0 B";
    public string ModifiedAt { get; init; } = string.Empty;
}
