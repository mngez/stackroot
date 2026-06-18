using System.Collections.ObjectModel;

using System.Windows.Media;

using Stackroot.App.Commands;

using Stackroot.Core.Abstractions;

using Stackroot.Core.Settings;

namespace Stackroot.App.ViewModels;



public sealed class ServiceEntryViewModel : ViewModelBase

{

    private string _statusText = "Stopped";

    private string _statusColor = "#91A0B5";

    private bool _isBusy;

    private bool _installed;

    private bool _enabled;

    private bool _isInstalling;

    private bool _hasInstalledVersion;

    private int _installPercent;

    private string? _message;

    private string? _installPhase;

    private string _selectedPackageId = string.Empty;

    private bool _isUpdatingPackage;



    public required ServiceDefinition Definition { get; init; }

    private ServicePortSettings _settings = null!;
    public required ServicePortSettings Settings
    {
        get => _settings;
        set
        {
            if (SetProperty(ref _settings, value))
            {
                RaisePropertyChanged(nameof(IsSupervised));
            }
        }
    }

    public required RelayCommand StartCommand { get; init; }

    public required RelayCommand StopCommand { get; init; }

    public required RelayCommand RestartCommand { get; init; }

    public RelayCommand? InstallCommand { get; init; }

    public RelayCommand? VersionsCommand { get; init; }

    public RelayCommand? SettingsCommand { get; init; }



    public ObservableCollection<ServicePackageOptionViewModel> InstalledVersions { get; } = [];



    public Action<string?>? OnPackageSelected { get; set; }



    public string Name => Definition.Name;

    public bool IsSupervised => Settings.Supervise && Settings.Enabled && Installed;

    public string Description => Definition.Description;

    public int Port => Settings.Port;

    public int? SslPort => Settings.SslPort;

    public ServiceCategory Category => Definition.Category;

    public string ActivePackageId => Settings.PackageId ?? Definition.PackageId ?? "—";



    private string? _detailsOverride;

    public string? DetailsOverride
    {
        get => _detailsOverride;
        set
        {
            if (SetProperty(ref _detailsOverride, value))
            {
                RaisePropertyChanged(nameof(DetailsText));
                RaisePropertyChanged(nameof(HasDetailsText));
            }
        }
    }

    public string DetailsText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(DetailsOverride))
            {
                return DetailsOverride;
            }

            if (!HasInstalledVersion)
            {
                return string.Empty;
            }

            var parts = new List<string> { $"Active: {ActivePackageLabel}" };

            if (Definition.DefaultPort > 0)

            {

                parts.Add($"{Settings.Host}:{Port}");

            }



            if (Settings.AutoStart)

            {

                parts.Add("Auto-start");

            }



            return string.Join(" · ", parts);

        }

    }

    public bool HasDetailsText => !string.IsNullOrWhiteSpace(DetailsText);



    private string _activePackageLabel = "—";

    public string ActivePackageLabel

    {

        get => _activePackageLabel;

        set

        {

            if (SetProperty(ref _activePackageLabel, value))

            {

                RaisePropertyChanged(nameof(DetailsText));

            }

        }

    }



    public string SelectedPackageId

    {

        get => _selectedPackageId;

        set

        {

            if (_isUpdatingPackage || string.Equals(_selectedPackageId, value, StringComparison.OrdinalIgnoreCase))

            {

                return;

            }



            if (SetProperty(ref _selectedPackageId, value))

            {

                OnPackageSelected?.Invoke(value);

            }

        }

    }



    public void SetSelectedPackageIdWithoutCallback(string? packageId)

    {

        _isUpdatingPackage = true;

        try

        {

            _selectedPackageId = packageId ?? string.Empty;

            RaisePropertyChanged(nameof(SelectedPackageId));

        }

        finally

        {

            _isUpdatingPackage = false;

        }

    }



    public bool Installed

    {

        get => _installed;

        set

        {

            if (SetProperty(ref _installed, value))

            {

                RaiseVisibilityChanged();

                RefreshCommandStates();

            }

        }

    }



    public bool Enabled

    {

        get => _enabled;

        set

        {

            if (SetProperty(ref _enabled, value))

            {

                RaiseVisibilityChanged();

                RefreshCommandStates();

                RaisePropertyChanged(nameof(DetailsText));

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

                RefreshCommandStates();

                NotifyRunningStateChanged();

            }

        }

    }



    public bool IsInstalling

    {

        get => _isInstalling;

        set

        {

            if (SetProperty(ref _isInstalling, value))

            {

                RaiseVisibilityChanged();

                RefreshCommandStates();

                NotifyRunningStateChanged();

                ServiceStatusPresenter.Apply(this, IsRunning);

            }

        }

    }



    public int InstallPercent

    {

        get => _installPercent;

        set => SetProperty(ref _installPercent, value);

    }



    public string? InstallPhase

    {

        get => _installPhase;

        set => SetProperty(ref _installPhase, value);

    }



    public string StatusText

    {

        get => _statusText;

        set

        {

            if (SetProperty(ref _statusText, value))

            {

                NotifyRunningStateChanged();

            }

        }

    }



    public string StatusColor

    {

        get => _statusColor;

        set => SetProperty(ref _statusColor, value);

    }



    public string? Message

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



    public bool HasInstalledVersion

    {

        get => _hasInstalledVersion;

        set

        {

            if (SetProperty(ref _hasInstalledVersion, value))

            {

                RaiseVisibilityChanged();

            }

        }

    }



    public bool ShowInstallButton => !HasInstalledVersion && !IsInstalling;

    public bool ShowVersionsButton => HasInstalledVersion;

    public bool ShowControlButtons => Installed && Enabled;

    public bool ShowSettingsButton => true;

    public bool ShowInstallProgress => IsInstalling;

    public bool ShowVersionCombo => HasInstalledVersion && InstalledVersions.Count > 0;

    public bool IsRunning => StatusText is "Running" or "Ready";

    public bool IsLive => IsRunning;

    public bool ShowBusyIndicator => IsBusy;

    public bool ShowStartButton => ShowControlButtons && !IsRunning && !IsBusy && !IsInstalling;

    public bool ShowStopButton => ShowControlButtons && IsRunning && !IsBusy && !IsInstalling;

    public bool ShowRestartButton => ShowControlButtons && IsRunning && !IsBusy && !IsInstalling;



    public System.Windows.Media.Brush IndicatorColor => CreateBrush(

        IsRunning ? "#4CAE8C" :

        StatusText == "Error" ? "#E88A92" :

        IsBusy ? "#E9BD5B" :

        "#91A0B5");

    public System.Windows.Media.Brush RowBackgroundBrush => CreateBrush(
        !HasInstalledVersion ? "#121A26" :
        IsRunning ? "#142019" :
        Enabled ? "#141A24" :
        "#10151E");

    public System.Windows.Media.Brush RowBorderBrush => CreateBrush(
        IsRunning ? "#4D4CAE8C" :
        StatusText == "Error" ? "#59E88A92" :
        IsBusy ? "#66E9BD5B" :
        HasInstalledVersion && Enabled ? "#353F52" :
        HasInstalledVersion ? "#2A3140" :
        "#263348");

    public double RowOpacity => HasInstalledVersion && !Enabled ? 0.88 : 1;



    public void NotifyVersionComboChanged()
    {
        RaisePropertyChanged(nameof(ShowVersionCombo));
    }

    public void NotifyRunningStateChanged()

    {

        RaisePropertyChanged(nameof(IsRunning));

        RaisePropertyChanged(nameof(IsLive));
        RaisePropertyChanged(nameof(ShowBusyIndicator));

        RaisePropertyChanged(nameof(ShowStartButton));

        RaisePropertyChanged(nameof(ShowStopButton));

        RaisePropertyChanged(nameof(ShowRestartButton));

        RaisePropertyChanged(nameof(IndicatorColor));

        RaisePropertyChanged(nameof(RowBackgroundBrush));

        RaisePropertyChanged(nameof(RowBorderBrush));

        RaisePropertyChanged(nameof(RowOpacity));

    }



    public void RefreshCommandStates()

    {

        SettingsCommand?.RaiseCanExecuteChanged();

        InstallCommand?.RaiseCanExecuteChanged();

        VersionsCommand?.RaiseCanExecuteChanged();

        StartCommand?.RaiseCanExecuteChanged();

        StopCommand?.RaiseCanExecuteChanged();

        RestartCommand?.RaiseCanExecuteChanged();

    }



    private void RaiseVisibilityChanged()

    {

        RaisePropertyChanged(nameof(ShowInstallButton));

        RaisePropertyChanged(nameof(ShowVersionsButton));

        RaisePropertyChanged(nameof(ShowControlButtons));

        RaisePropertyChanged(nameof(ShowSettingsButton));

        RaisePropertyChanged(nameof(ShowInstallProgress));

        RaisePropertyChanged(nameof(ShowVersionCombo));

        RaisePropertyChanged(nameof(RowBackgroundBrush));

        RaisePropertyChanged(nameof(RowBorderBrush));

        RaisePropertyChanged(nameof(RowOpacity));

    }



    private static SolidColorBrush CreateBrush(string hex) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);
}


