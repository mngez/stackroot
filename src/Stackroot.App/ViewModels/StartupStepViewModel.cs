namespace Stackroot.App.ViewModels;

public enum StartupStepStatus
{
    Pending,
    Running,
    Completed,
    Failed
}

public sealed class StartupStepViewModel : ViewModelBase
{
    private StartupStepStatus _status = StartupStepStatus.Pending;
    private string? _detail;

    public required string Id { get; init; }

    public required string Title { get; init; }

    public StartupStepStatus Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                RaisePropertyChanged(nameof(StatusGlyph));
                RaisePropertyChanged(nameof(StatusBrushKey));
                RaisePropertyChanged(nameof(IsRunning));
                RaisePropertyChanged(nameof(IsCompleted));
                RaisePropertyChanged(nameof(IsFailed));
            }
        }
    }

    public string? Detail
    {
        get => _detail;
        set => SetProperty(ref _detail, value);
    }

    public bool IsRunning => Status == StartupStepStatus.Running;

    public bool IsCompleted => Status == StartupStepStatus.Completed;

    public bool IsFailed => Status == StartupStepStatus.Failed;

    public string StatusGlyph => Status switch
    {
        StartupStepStatus.Running => "\uE768",
        StartupStepStatus.Completed => "\uE73E",
        StartupStepStatus.Failed => "\uE711",
        _ => "\uE739"
    };

    public string StatusBrushKey => Status switch
    {
        StartupStepStatus.Running => "StackrootAccentTextBrush",
        StartupStepStatus.Completed => "StackrootAccentTextBrush",
        StartupStepStatus.Failed => "StackrootDangerTextBrush",
        _ => "StackrootTextMutedBrush"
    };
}
