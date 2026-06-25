using Stackroot.App.Commands;
using Stackroot.Core.Services.Php;
using Stackroot.Core.Settings;

namespace Stackroot.App.ViewModels;

public sealed class PhpRuntimeSettingsDialogViewModel : ViewModelBase
{
    private readonly PhpExtensionManager _extensionManager;
    private readonly SettingsStore _settingsStore;
    private string _fpmHost = "127.0.0.1";
    private int _fpmPort = 9000;
    private int _fpmPoolSize = 2;
    private string _statusMessage = string.Empty;
    private bool _isBusy;

    public PhpRuntimeSettingsDialogViewModel(PhpExtensionManager extensionManager, SettingsStore settingsStore)
    {
        _extensionManager = extensionManager;
        _settingsStore = settingsStore;

        var settings = settingsStore.Load();
        _fpmHost = settings.Php.FpmHost;
        _fpmPort = settings.Php.FpmPort <= 0 ? 9000 : settings.Php.FpmPort;
        _fpmPoolSize = settings.Php.FpmPoolSize is >= 1 and <= 8 ? settings.Php.FpmPoolSize : 2;

        SaveCommand = new RelayCommand(_ => Save(), _ => !IsBusy);
        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty));
    }

    public string FpmHost
    {
        get => _fpmHost;
        set => SetProperty(ref _fpmHost, value);
    }

    public int FpmPort
    {
        get => _fpmPort;
        set => SetProperty(ref _fpmPort, value);
    }

    /// <summary>php-cgi workers per PHP version. More than one gives nginx a pool to fail over to when a worker recycles.</summary>
    public int FpmPoolSize
    {
        get => _fpmPoolSize;
        set => SetProperty(ref _fpmPoolSize, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public RelayCommand SaveCommand { get; }
    public RelayCommand CloseCommand { get; }

    public event EventHandler? RequestClose;
    public event EventHandler? SettingsSaved;

    private void Save()
    {
        if (FpmPort is < 1 or > 65535)
        {
            StatusMessage = "Port must be between 1 and 65535.";
            return;
        }

        if (string.IsNullOrWhiteSpace(FpmHost))
        {
            StatusMessage = "FastCGI host is required.";
            return;
        }

        if (FpmPoolSize is < 1 or > 8)
        {
            StatusMessage = "Workers per version must be between 1 and 8.";
            return;
        }

        IsBusy = true;
        try
        {
            _extensionManager.SaveRuntimeSettings(FpmHost.Trim(), FpmPort, FpmPoolSize);
            StatusMessage = "Runtime settings saved. Restart FastCGI if it is already running.";
            SettingsSaved?.Invoke(this, EventArgs.Empty);
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
