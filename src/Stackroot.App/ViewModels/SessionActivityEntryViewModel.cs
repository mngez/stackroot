using Stackroot.App.Services;

namespace Stackroot.App.ViewModels;

public sealed class SessionActivityEntryViewModel : ViewModelBase
{
    private string _message = string.Empty;
    private SessionActivityTone _tone;

    public required Guid Id { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value);
    }

    public SessionActivityTone Tone
    {
        get => _tone;
        set
        {
            if (SetProperty(ref _tone, value))
            {
                RaisePropertyChanged(nameof(ToneColor));
                RaisePropertyChanged(nameof(IsProgress));
            }
        }
    }

    public string TimeLabel => Timestamp.LocalDateTime.ToString("HH:mm");

    public bool IsProgress => Tone == SessionActivityTone.Progress;

    public string ToneColor => Tone switch
    {
        SessionActivityTone.Success => "#8FD6B6",
        SessionActivityTone.Error => "#EAAAB0",
        SessionActivityTone.Warning => "#E9BD5B",
        SessionActivityTone.Progress => "#E9BD5B",
        _ => "#91A0B5"
    };
}
