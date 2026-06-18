using System.Windows.Media;

namespace Stackroot.App.ViewModels;

public sealed class NodeVersionRowViewModel : ViewModelBase
{
    private string _version = string.Empty;
    private bool _isActive;
    private bool _isRemoving;

    public string Version
    {
        get => _version;
        set
        {
            if (SetProperty(ref _version, value))
            {
                RaisePresentationChanged();
            }
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value))
            {
                RaisePresentationChanged();
            }
        }
    }

    public bool IsRemoving
    {
        get => _isRemoving;
        private set
        {
            if (SetProperty(ref _isRemoving, value))
            {
                RaisePresentationChanged();
            }
        }
    }

    public void SetRemoving(bool removing) => IsRemoving = removing;

    public bool ShowActiveBadge => IsActive;
    public bool ShowActivateButton => !IsActive && !IsRemoving;
    public bool CanInteract => !IsRemoving;
    public string UninstallButtonLabel => IsRemoving ? "Removing…" : "Remove";

    public System.Windows.Media.Brush RowBorderBrush =>
        IsActive
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAE, 0x8C))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x26, 0x33, 0x48));

    public System.Windows.Media.Brush StatusBackgroundBrush =>
        IsActive
            ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1A, 0x4C, 0xAE, 0x8C))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x12, 0x1A, 0x26));

    public System.Windows.Media.Brush StatusForegroundBrush =>
        IsActive
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8F, 0xD6, 0xB6))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x91, 0xA0, 0xB5));

    public string StatusLabel => IsActive ? "Active" : "Installed";

    private void RaisePresentationChanged()
    {
        RaisePropertyChanged(nameof(ShowActiveBadge));
        RaisePropertyChanged(nameof(ShowActivateButton));
        RaisePropertyChanged(nameof(CanInteract));
        RaisePropertyChanged(nameof(UninstallButtonLabel));
        RaisePropertyChanged(nameof(RowBorderBrush));
        RaisePropertyChanged(nameof(StatusBackgroundBrush));
        RaisePropertyChanged(nameof(StatusForegroundBrush));
        RaisePropertyChanged(nameof(StatusLabel));
    }
}
