using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Stackroot.App.Commands;
using Stackroot.Core.Catalog;

namespace Stackroot.App.ViewModels;

public sealed class DownloadsViewModel : ViewModelBase
{
    private readonly DownloadCacheStore _downloadCache;
    private string _statusMessage = string.Empty;
    private string _cachePath = string.Empty;
    private string _totalText = "0 B";

    public DownloadsViewModel(DownloadCacheStore downloadCache)
    {
        _downloadCache = downloadCache;

        Items = [];
        RefreshCommand = new RelayCommand(_ => Refresh());
        RemoveCommand = new RelayCommand(row => Remove(row as DownloadRowViewModel));
        OpenFolderCommand = new RelayCommand(_ => OpenFolder());

        Refresh();
    }

    public ObservableCollection<DownloadRowViewModel> Items { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    public string CachePath
    {
        get => _cachePath;
        private set => SetProperty(ref _cachePath, value);
    }

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

    public void Refresh()
    {
        CachePath = _downloadCache.CacheRoot;

        Items.Clear();
        long totalBytes = 0;
        foreach (var entry in _downloadCache.List())
        {
            totalBytes += entry.SizeBytes;
            Items.Add(new DownloadRowViewModel
            {
                PackageId = entry.PackageId,
                FileName = entry.FileName,
                Url = entry.Url ?? string.Empty,
                SizeText = FormatBytes(entry.SizeBytes),
                DownloadedAt = FormatDownloadedAt(entry.DownloadedAt)
            });
        }

        TotalText = $"{Items.Count} files - {FormatBytes(totalBytes)}";
        StatusMessage = string.Empty;
    }

    private void Remove(DownloadRowViewModel? row)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.FileName))
        {
            return;
        }

        if (_downloadCache.Remove(row.FileName))
        {
            StatusMessage = $"Removed {row.FileName}.";
            Refresh();
            return;
        }

        StatusMessage = $"Could not remove {row.FileName}.";
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(_downloadCache.CacheRoot);
            Process.Start(new ProcessStartInfo
            {
                FileName = _downloadCache.CacheRoot,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }

    private static string FormatDownloadedAt(string value)
    {
        if (DateTimeOffset.TryParse(value, out var parsed))
        {
            return parsed.ToLocalTime().ToString("g");
        }

        return value;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024d:F1} KB";
        if (bytes < 1024L * 1024L * 1024L) return $"{bytes / (1024d * 1024d):F1} MB";
        return $"{bytes / (1024d * 1024d * 1024d):F2} GB";
    }
}

public sealed class DownloadRowViewModel
{
    public string PackageId { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string SizeText { get; init; } = "0 B";
    public string DownloadedAt { get; init; } = string.Empty;
}
