using System.Globalization;
using Stackroot.App.Commands;
using Stackroot.App.Services;

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
                or nameof(SessionActivityService.HasItems)
                or nameof(SessionActivityService.ActiveCount)
                or nameof(SessionActivityService.HasActiveOperations))
            {
                RefreshBadge();
            }
        };

        _activity.ActivityChanged += (_, _) =>
        {
            RefreshBadge();
            PulseRequested?.Invoke(this, EventArgs.Empty);
        };
        _activity.Items.CollectionChanged += (_, _) =>
        {
            RaisePropertyChanged(nameof(HasItems));
            RaisePropertyChanged(nameof(ShowEmptyPanel));
            RaisePropertyChanged(nameof(ActiveCount));
            RaisePropertyChanged(nameof(ShowActiveFooter));
            RaisePropertyChanged(nameof(ActiveFooterLabel));
            ClearCommand.RaiseCanExecuteChanged();
        };
    }

    public event EventHandler? PulseRequested;

    public SessionActivityService Activity => _activity;

    public int UnreadCount => _activity.UnreadCount;

    public int ActiveCount => _activity.ActiveCount;

    public bool ShowActiveBadge => ActiveCount > 0;

    public string BadgeLabel
    {
        get
        {
            if (ActiveCount > 0)
            {
                return FormatBadgeCount(ActiveCount);
            }

            return UnreadCount > 0 ? FormatBadgeCount(UnreadCount) : string.Empty;
        }
    }

    public bool ShowBadge => _activity.ShowBadge;

    public bool HasItems => _activity.HasItems;

    public bool ShowActiveFooter => ActiveCount > 0;

    public string ActiveFooterLabel => ActiveCount == 1
        ? "1 operation running"
        : $"{ActiveCount} operations running";

    public string TrayToolTip
    {
        get
        {
            var parts = new List<string>();
            if (ActiveCount > 0)
            {
                parts.Add(ActiveCount == 1 ? "1 running" : $"{ActiveCount} running");
            }

            if (UnreadCount > 0)
            {
                parts.Add(UnreadCount == 1 ? "1 new" : $"{UnreadCount} new");
            }

            return parts.Count > 0
                ? $"{string.Join(" · ", parts)} — click to view"
                : "Activity log";
        }
    }

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (SetProperty(ref _isOpen, value))
            {
                _activity.IsTrayOpen = value;
                if (value)
                {
                    _activity.MarkAllRead();
                }

                RaisePropertyChanged(nameof(ShowEmptyPanel));
                RefreshBadge();
            }
        }
    }

    public bool ShowEmptyPanel => IsOpen && !HasItems;

    public RelayCommand ToggleCommand { get; }
    public RelayCommand CloseCommand { get; }
    public RelayCommand ClearCommand { get; }

    private void Toggle() => IsOpen = !IsOpen;

    private void RefreshBadge()
    {
        RaisePropertyChanged(nameof(ShowBadge));
        RaisePropertyChanged(nameof(ShowActiveBadge));
        RaisePropertyChanged(nameof(UnreadCount));
        RaisePropertyChanged(nameof(ActiveCount));
        RaisePropertyChanged(nameof(BadgeLabel));
        RaisePropertyChanged(nameof(TrayToolTip));
        RaisePropertyChanged(nameof(ShowActiveFooter));
        RaisePropertyChanged(nameof(ActiveFooterLabel));
        ClearCommand.RaiseCanExecuteChanged();
    }

    private static string FormatBadgeCount(int count) =>
        count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture);
}
