using System.Windows.Media;
using Stackroot.App.Commands;

namespace Stackroot.App.ViewModels;

public sealed class AdminUiToolViewModel : ViewModelBase
{
    private string _description = string.Empty;
    private string _detailsText = string.Empty;
    private string _statusText = "Unknown";
    private string _statusColor = "#91A0B5";
    private string _message = string.Empty;
    private string _badgeText = string.Empty;
    private bool _installed;
    private bool _isReady;
    private bool _isBusy;
    private bool _showInstallButton = true;
    private bool _showVersionsButton;
    private bool _showOpenButton;
    private int _installPercent;

    public required string Name { get; init; }
    public required RelayCommand SettingsCommand { get; init; }
    public required RelayCommand InstallCommand { get; init; }
    public required RelayCommand VersionsCommand { get; init; }
    public required RelayCommand OpenCommand { get; init; }

    public string Description
    {
        get => _description;
        set
        {
            if (SetProperty(ref _description, value))
            {
                RaisePropertyChanged(nameof(ShowDescription));
            }
        }
    }

    public bool ShowDescription => !string.IsNullOrWhiteSpace(Description);

    public string DetailsText
    {
        get => _detailsText;
        set
        {
            if (SetProperty(ref _detailsText, value))
            {
                RaisePropertyChanged(nameof(HasDetailsText));
            }
        }
    }

    public bool HasDetailsText => !string.IsNullOrWhiteSpace(DetailsText);

    public string StatusText
    {
        get => _statusText;
        set
        {
            if (SetProperty(ref _statusText, value))
            {
                RaisePropertyChanged(nameof(IndicatorColor));
            }
        }
    }

    public string StatusColor
    {
        get => _statusColor;
        set => SetProperty(ref _statusColor, value);
    }

    public System.Windows.Media.Brush IndicatorColor =>
        _isReady
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAE, 0x8C))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x91, 0xA0, 0xB5));

    public string Message
    {
        get => _message;
        set
        {
            if (SetProperty(ref _message, value))
            {
                RaisePropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    public string BadgeText
    {
        get => _badgeText;
        set
        {
            if (SetProperty(ref _badgeText, value))
            {
                RaisePropertyChanged(nameof(ShowBadge));
            }
        }
    }

    public bool ShowBadge => !string.IsNullOrWhiteSpace(BadgeText);

    public bool Installed
    {
        get => _installed;
        set
        {
            if (SetProperty(ref _installed, value))
            {
                RaisePropertyChanged(nameof(IndicatorColor));
                RaisePropertyChanged(nameof(RowBackgroundBrush));
                RaisePropertyChanged(nameof(RowBorderBrush));
            }
        }
    }

    public bool IsReady
    {
        get => _isReady;
        set
        {
            if (SetProperty(ref _isReady, value))
            {
                RaisePropertyChanged(nameof(ShowLiveBadge));
                RaisePropertyChanged(nameof(IndicatorColor));
                RaisePropertyChanged(nameof(RowBackgroundBrush));
                RaisePropertyChanged(nameof(RowBorderBrush));
            }
        }
    }

    public bool ShowLiveBadge => IsReady;

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaisePropertyChanged(nameof(ShowInstallProgress));
                RaisePropertyChanged(nameof(RowBackgroundBrush));
                RaisePropertyChanged(nameof(RowBorderBrush));
                SettingsCommand.RaiseCanExecuteChanged();
                InstallCommand.RaiseCanExecuteChanged();
                VersionsCommand.RaiseCanExecuteChanged();
                OpenCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool ShowInstallButton
    {
        get => _showInstallButton;
        set => SetProperty(ref _showInstallButton, value);
    }

    public bool ShowVersionsButton
    {
        get => _showVersionsButton;
        set => SetProperty(ref _showVersionsButton, value);
    }

    public bool ShowOpenButton
    {
        get => _showOpenButton;
        set => SetProperty(ref _showOpenButton, value);
    }

    public int InstallPercent
    {
        get => _installPercent;
        set => SetProperty(ref _installPercent, value);
    }

    public bool ShowInstallProgress => IsBusy;

    public bool ShowPhpSelector
    {
        get => _showPhpSelector;
        set
        {
            if (SetProperty(ref _showPhpSelector, value))
            {
                RaisePropertyChanged(nameof(ShowPhpSelector));
            }
        }
    }

    private bool _showPhpSelector;

    public System.Windows.Media.Brush RowBackgroundBrush =>
        IsBusy
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x24, 0x33))
            : IsReady
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x14, 0x20, 0x19))
                : Installed
                    ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x12, 0x1A, 0x26))
                    : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x14, 0x1C, 0x28));

    public System.Windows.Media.Brush RowBorderBrush =>
        IsReady
            ? new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x4D, 0x4C, 0xAE, 0x8C))
            : Installed
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x35, 0x3F, 0x52))
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x33, 0x40));
}
