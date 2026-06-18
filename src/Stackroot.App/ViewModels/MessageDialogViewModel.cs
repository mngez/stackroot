using Stackroot.App.Commands;

namespace Stackroot.App.ViewModels;

public enum StackrootDialogKind
{
    Error,
    Warning,
    Info,
    Question
}

public enum StackrootDialogButtons
{
    Ok,
    YesNo,
    YesNoCancel
}

public enum StackrootDialogResult
{
    None,
    Ok,
    Yes,
    No,
    Cancel
}

public sealed class MessageDialogViewModel : ViewModelBase
{
    public MessageDialogViewModel(
        string title,
        string message,
        StackrootDialogKind kind,
        StackrootDialogButtons buttons = StackrootDialogButtons.Ok,
        string? details = null,
        string okText = "OK",
        string yesText = "Yes",
        string noText = "No",
        string cancelText = "Cancel")
    {
        Title = title;
        Message = message;
        Kind = kind;
        Buttons = buttons;
        Details = details;
        OkText = okText;
        YesText = yesText;
        NoText = noText;
        CancelText = cancelText;

        OkCommand = new RelayCommand(_ => RequestClose?.Invoke(this, StackrootDialogResult.Ok));
        YesCommand = new RelayCommand(_ => RequestClose?.Invoke(this, StackrootDialogResult.Yes));
        NoCommand = new RelayCommand(_ => RequestClose?.Invoke(this, StackrootDialogResult.No));
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(this, StackrootDialogResult.Cancel));
    }

    public string Title { get; }
    public string Message { get; }
    public string? Details { get; }
    public StackrootDialogKind Kind { get; }
    public StackrootDialogButtons Buttons { get; }
    public string OkText { get; }
    public string YesText { get; }
    public string NoText { get; }
    public string CancelText { get; }

    public string IconGlyph => Kind switch
    {
        StackrootDialogKind.Error => "\uE783",
        StackrootDialogKind.Warning => "\uE7BA",
        StackrootDialogKind.Question => "\uE897",
        _ => "\uE946"
    };

    public RelayCommand OkCommand { get; }
    public RelayCommand YesCommand { get; }
    public RelayCommand NoCommand { get; }
    public RelayCommand CancelCommand { get; }

    public event EventHandler<StackrootDialogResult>? RequestClose;
}
