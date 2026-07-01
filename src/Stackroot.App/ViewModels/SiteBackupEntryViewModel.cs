using Stackroot.App.Commands;
using Stackroot.App.Services;
using System.Windows.Input;

namespace Stackroot.App.ViewModels;

public sealed class SiteBackupEntryViewModel
{
    public string FileName { get; }
    public string DateDisplay { get; }
    public string SizeDisplay { get; }
    public ICommand RestoreCommand { get; }
    public ICommand DeleteCommand { get; }

    public SiteBackupEntryViewModel(
        SiteBackupEntry entry,
        Action<SiteBackupEntry> restore,
        Action<SiteBackupEntry> delete,
        Func<bool>? canExecute = null)
    {
        FileName = entry.FileName;
        DateDisplay = entry.Date != default
            ? entry.Date.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "—";
        SizeDisplay = FormatSize(entry.SizeBytes);
        RestoreCommand = new RelayCommand(_ => restore(entry), _ => canExecute?.Invoke() ?? true);
        DeleteCommand = new RelayCommand(_ => delete(entry), _ => canExecute?.Invoke() ?? true);
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024
            ? $"{bytes / (1024.0 * 1024):F1} MB"
            : $"{bytes / 1024.0:F0} KB";
}
