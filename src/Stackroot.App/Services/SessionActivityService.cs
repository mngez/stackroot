using System.Collections.ObjectModel;
using System.Windows;
using Stackroot.App.ViewModels;

namespace Stackroot.App.Services;

public sealed class SessionActivityService : ViewModelBase
{
    private const int MaxItems = 80;

    private int _unreadCount;

    public SessionActivityService()
    {
        Items = [];
        Items.CollectionChanged += (_, _) =>
        {
            RaisePropertyChanged(nameof(HasItems));
            RaisePropertyChanged(nameof(ActiveCount));
            RaisePropertyChanged(nameof(HasActiveOperations));
        };
    }

    public event EventHandler? ActivityChanged;

    public ObservableCollection<SessionActivityEntryViewModel> Items { get; }

    /// <summary>True while the activity popup is open (unread counter is frozen).</summary>
    public bool IsTrayOpen { get; set; }

    public int UnreadCount
    {
        get => _unreadCount;
        private set
        {
            if (SetProperty(ref _unreadCount, value))
            {
                RaisePropertyChanged(nameof(ShowBadge));
            }
        }
    }

    public int ActiveCount => Items.Count(item => item.Tone == SessionActivityTone.Progress);

    public bool HasActiveOperations => ActiveCount > 0;

    public bool ShowBadge => UnreadCount > 0 || HasActiveOperations;

    public bool HasItems => Items.Count > 0;

    public Guid Begin(string message)
    {
        var id = Guid.NewGuid();
        RunOnUi(() =>
        {
            var entry = CreateEntry(message, SessionActivityTone.Progress, id);
            Items.Insert(0, entry);
            TrimItems();
            NotifyActivityChanged();
        });
        return id;
    }

    public void Complete(Guid id, string message, SessionActivityTone tone = SessionActivityTone.Success)
    {
        RunOnUi(() =>
        {
            UpdateEntry(id, message, tone);
            BumpUnread();
            NotifyActivityChanged();
        });
    }

    public void Fail(Guid id, string message) => Complete(id, message, SessionActivityTone.Error);

    public void UpdateProgress(Guid id, string message) =>
        RunOnUi(() => UpdateEntry(id, message, SessionActivityTone.Progress));

    public void Log(string message, SessionActivityTone tone = SessionActivityTone.Info)
    {
        RunOnUi(() =>
        {
            Items.Insert(0, CreateEntry(message, tone));
            TrimItems();
            BumpUnread();
            NotifyActivityChanged();
        });
    }

    public async Task RunAsync(
        string progressMessage,
        Func<Task> work,
        string successMessage,
        SessionActivityTone successTone = SessionActivityTone.Success)
    {
        var id = Begin(progressMessage);
        try
        {
            await work().ConfigureAwait(true);
            Complete(id, successMessage, successTone);
        }
        catch (Exception ex)
        {
            Fail(id, ex.Message);
        }
    }

    public void MarkAllRead() => UnreadCount = 0;

    public void Clear()
    {
        RunOnUi(() =>
        {
            Items.Clear();
            UnreadCount = 0;
            NotifyActivityChanged();
        });
    }

    private SessionActivityEntryViewModel CreateEntry(string message, SessionActivityTone tone, Guid? id = null)
    {
        var entry = new SessionActivityEntryViewModel
        {
            Id = id ?? Guid.NewGuid(),
            Timestamp = DateTimeOffset.Now,
            Message = message,
            Tone = tone
        };
        entry.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SessionActivityEntryViewModel.Tone))
            {
                RaisePropertyChanged(nameof(ActiveCount));
                RaisePropertyChanged(nameof(HasActiveOperations));
                RaisePropertyChanged(nameof(ShowBadge));
            }
        };
        return entry;
    }

    private void UpdateEntry(Guid id, string message, SessionActivityTone tone)
    {
        var entry = Items.FirstOrDefault(item => item.Id == id)
            ?? Items.FirstOrDefault(item => item.Tone == SessionActivityTone.Progress);
        if (entry is null)
        {
            Items.Insert(0, CreateEntry(message, tone));
            TrimItems();
            return;
        }

        entry.Message = message;
        entry.Tone = tone;
    }

    private void BumpUnread()
    {
        if (!IsTrayOpen)
        {
            UnreadCount++;
        }
    }

    private void NotifyActivityChanged()
    {
        RaisePropertyChanged(nameof(ActiveCount));
        RaisePropertyChanged(nameof(HasActiveOperations));
        RaisePropertyChanged(nameof(ShowBadge));
        ActivityChanged?.Invoke(this, EventArgs.Empty);
    }

    private void TrimItems()
    {
        while (Items.Count > MaxItems)
        {
            Items.RemoveAt(Items.Count - 1);
        }
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action, System.Windows.Threading.DispatcherPriority.Background);
    }
}
