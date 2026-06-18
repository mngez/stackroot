using System.Collections.ObjectModel;
using Stackroot.App.Commands;
using Stackroot.App.Services;

namespace Stackroot.App.ViewModels;

public sealed class DownloadTrayViewModel : ViewModelBase
{
    private readonly InstallProgressTracker _tracker;
    private bool _isOpen;

    public DownloadTrayViewModel(InstallProgressTracker tracker)
    {
        _tracker = tracker;
        ToggleCommand = new RelayCommand(_ => IsOpen = !IsOpen);
        CloseCommand = new RelayCommand(_ => IsOpen = false);
        _tracker.Changed += OnTrackerChanged;
    }

    public ObservableCollection<InstallQueueItemViewModel> Items => _tracker.Items;

    public int ActiveCount => _tracker.ActiveCount;

    public bool HasItems => Items.Count > 0;

    public bool ShowBadge => ActiveCount > 0;

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (SetProperty(ref _isOpen, value))
            {
                RaisePropertyChanged(nameof(ShowEmptyPanel));
            }
        }
    }

    public bool ShowEmptyPanel => IsOpen && !HasItems;

    public RelayCommand ToggleCommand { get; }
    public RelayCommand CloseCommand { get; }

    private void OnTrackerChanged(object? sender, EventArgs e)
    {
        RaisePropertyChanged(nameof(ActiveCount));
        RaisePropertyChanged(nameof(HasItems));
        RaisePropertyChanged(nameof(ShowBadge));
        RaisePropertyChanged(nameof(ShowEmptyPanel));
    }
}
