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
        Items.CollectionChanged += (_, _) => RaisePropertyChanged(nameof(HasItems));
    }

    public ObservableCollection<SessionActivityEntryViewModel> Items { get; }

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

    public bool ShowBadge => UnreadCount > 0;

    public bool HasItems => Items.Count > 0;

    public Guid Begin(string message)
    {
        var id = Guid.NewGuid();
        RunOnUi(() =>
        {
            var entry = CreateEntry(message, SessionActivityTone.Progress, id);
            Items.Insert(0, entry);
            TrimItems();
            UnreadCount++;
        });
        return id;
    }

    public void Complete(Guid id, string message, SessionActivityTone tone = SessionActivityTone.Success)
    {
        RunOnUi(() => UpdateEntry(id, message, tone));
    }

    public void Fail(Guid id, string message) => Complete(id, message, SessionActivityTone.Error);

    public void Log(string message, SessionActivityTone tone = SessionActivityTone.Info)
    {
        RunOnUi(() =>
        {
            Items.Insert(0, CreateEntry(message, tone));
            TrimItems();
            UnreadCount++;
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
        });
    }

    private SessionActivityEntryViewModel CreateEntry(string message, SessionActivityTone tone, Guid? id = null) =>
        new()
        {
            Id = id ?? Guid.NewGuid(),
            Timestamp = DateTimeOffset.Now,
            Message = message,
            Tone = tone
        };

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
