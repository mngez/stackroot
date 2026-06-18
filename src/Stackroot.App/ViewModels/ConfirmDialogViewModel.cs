using Stackroot.App.Commands;

namespace Stackroot.App.ViewModels;

public sealed class ConfirmDialogViewModel : ViewModelBase
{
    public ConfirmDialogViewModel(string title, string message, string confirmText, bool isDanger, string? checkboxLabel = null)
    {
        Title = title;
        Message = message;
        ConfirmText = confirmText;
        IsDanger = isDanger;
        CheckboxLabel = checkboxLabel;
        IsChecked = false;

        ConfirmCommand = new RelayCommand(_ => RequestClose?.Invoke(this, true));
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(this, false));
    }

    public string Title { get; }
    public string Message { get; }
    public string ConfirmText { get; }
    public bool IsDanger { get; }
    public string? CheckboxLabel { get; }
    public bool ShowCheckbox => !string.IsNullOrWhiteSpace(CheckboxLabel);

    private bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }

    public RelayCommand ConfirmCommand { get; }
    public RelayCommand CancelCommand { get; }

    public event EventHandler<bool>? RequestClose;
}
