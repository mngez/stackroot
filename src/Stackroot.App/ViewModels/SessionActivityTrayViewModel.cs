using Stackroot.App.Commands;
using Stackroot.App.Services;
using System.Globalization;

namespace Stackroot.App.ViewModels;

public sealed class SessionActivityTrayViewModel : ViewModelBase
{
    private readonly SessionActivityService _activity;
    private bool _isOpen;

    public SessionActivityTrayViewModel(SessionActivityService activity)
    {
        _activity = activity;
        ToggleCommand = new RelayCommand(_ => Toggle());
        CloseCommand = new RelayCommand(_ => IsOpen = false);
        ClearCommand = new RelayCommand(_ => _activity.Clear(), _ => _activity.HasItems);
        _activity.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SessionActivityService.UnreadCount)
                or nameof(SessionActivityService.ShowBadge)
                or nameof(SessionActivityService.HasItems))
            {
                RaisePropertyChanged(nameof(ShowBadge));
                RaisePropertyChanged(nameof(UnreadCount));
                RaisePropertyChanged(nameof(BadgeLabel));
                RaisePropertyChanged(nameof(HasItems));
                RaisePropertyChanged(nameof(ShowEmptyPanel));
                ClearCommand.RaiseCanExecuteChanged();
            }
        };
        _activity.Items.CollectionChanged += (_, _) =>
        {
            RaisePropertyChanged(nameof(HasItems));
            RaisePropertyChanged(nameof(ShowEmptyPanel));
            ClearCommand.RaiseCanExecuteChanged();
        };
    }

    public SessionActivityService Activity => _activity;

    public int UnreadCount => _activity.UnreadCount;

    public string BadgeLabel => UnreadCount > 9 ? "9+" : UnreadCount.ToString(CultureInfo.InvariantCulture);

    public bool ShowBadge => _activity.ShowBadge;

    public bool HasItems => _activity.HasItems;

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (SetProperty(ref _isOpen, value))
            {
                if (value)
                {
                    _activity.MarkAllRead();
                }

                RaisePropertyChanged(nameof(ShowEmptyPanel));
            }
        }
    }

    public bool ShowEmptyPanel => IsOpen && !HasItems;

    public RelayCommand ToggleCommand { get; }
    public RelayCommand CloseCommand { get; }
    public RelayCommand ClearCommand { get; }

    private void Toggle() => IsOpen = !IsOpen;
}
