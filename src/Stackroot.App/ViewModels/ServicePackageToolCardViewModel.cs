using Stackroot.App.Commands;
using Stackroot.Core.Abstractions;

namespace Stackroot.App.ViewModels;

public sealed class ServicePackageToolCardViewModel : ViewModelBase
{
    private string _versionLabel = "No version";
    private bool _showInstallButton = true;
    private bool _showVersionsButton;
    private bool _isBusy;

    public required string Name { get; init; }
    public string? Hint { get; init; }
    public required PackageType PackageType { get; init; }
    public required RelayCommand InstallCommand { get; init; }
    public required RelayCommand VersionsCommand { get; init; }

    public string VersionLabel
    {
        get => _versionLabel;
        set => SetProperty(ref _versionLabel, value);
    }

    public bool ShowInstallButton
    {
        get => _showInstallButton;
        set
        {
            if (SetProperty(ref _showInstallButton, value))
            {
                InstallCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool ShowVersionsButton
    {
        get => _showVersionsButton;
        set
        {
            if (SetProperty(ref _showVersionsButton, value))
            {
                VersionsCommand.RaiseCanExecuteChanged();
                RaisePropertyChanged(nameof(IndicatorColor));
                RaisePropertyChanged(nameof(RowBackgroundBrush));
                RaisePropertyChanged(nameof(RowBorderBrush));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                InstallCommand.RaiseCanExecuteChanged();
                VersionsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            if (SetProperty(ref _isDownloading, value))
            {
                InstallCommand.RaiseCanExecuteChanged();
                VersionsCommand.RaiseCanExecuteChanged();
                RaisePropertyChanged(nameof(ShowInstallProgress));
                RaisePropertyChanged(nameof(IndicatorColor));
                RaisePropertyChanged(nameof(RowBackgroundBrush));
            }
        }
    }

    private bool _isDownloading;
    private string? _statusNote;

    public string? StatusNote
    {
        get => _statusNote;
        set
        {
            if (SetProperty(ref _statusNote, value))
            {
                RaisePropertyChanged(nameof(ShowStatusNote));
            }
        }
    }

    public bool ShowStatusNote => !string.IsNullOrWhiteSpace(StatusNote);

    public bool ShowHint => !string.IsNullOrWhiteSpace(Hint);

    private int _installPercent;
    private string _installMessage = string.Empty;

    public int InstallPercent
    {
        get => _installPercent;
        set
        {
            if (SetProperty(ref _installPercent, value))
            {
                RaisePropertyChanged(nameof(ShowInstallProgress));
            }
        }
    }

    public string InstallMessage
    {
        get => _installMessage;
        set
        {
            if (SetProperty(ref _installMessage, value))
            {
                RaisePropertyChanged(nameof(HasInstallMessage));
            }
        }
    }

    public bool HasInstallMessage => !string.IsNullOrWhiteSpace(InstallMessage);

    public bool ShowInstallProgress => IsDownloading;

    public System.Windows.Media.Brush IndicatorColor =>
        IsDownloading
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE9, 0xBD, 0x5B))
            : ShowVersionsButton
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAE, 0x8C))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x91, 0xA0, 0xB5));

    public System.Windows.Media.Brush RowBackgroundBrush =>
        IsDownloading
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x24, 0x33))
            : ShowVersionsButton
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x12, 0x1A, 0x26))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x14, 0x1C, 0x28));

    public System.Windows.Media.Brush RowBorderBrush =>
        IsDownloading
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x45, 0x55))
            : ShowVersionsButton
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x35, 0x3F, 0x52))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x33, 0x40));

    public bool IsBlocked
    {
        get => _isBlocked;
        set
        {
            if (SetProperty(ref _isBlocked, value))
            {
                InstallCommand.RaiseCanExecuteChanged();
                VersionsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private bool _isBlocked;
}
