using Stackroot.App.Commands;
using Stackroot.App.Helpers;
using Stackroot.App.Views;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Settings;

namespace Stackroot.App.ViewModels;

public sealed class ServiceSettingsDialogViewModel : ViewModelBase
{
    private readonly SettingsStore _settingsStore;
    private readonly ServiceId _serviceId;
    private readonly ServiceDefinition _definition;
    private bool _enabled;
    private bool _autoStart;
    private bool _supervise;
    private string _host = "127.0.0.1";
    private int _port;
    private int? _sslPort;
    private bool _sslEnabled;
    private string _activePackageLabel = "—";
    private string _statusMessage = string.Empty;

    public ServiceSettingsDialogViewModel(
        SettingsStore settingsStore,
        ServiceDefinition definition,
        ServicePortSettings settings,
        string? activePackageLabel)
    {
        _settingsStore = settingsStore;
        _serviceId = definition.Id;
        _definition = definition;

        Title = $"{definition.Name} settings";
        ShowNetworkFields = definition.DefaultPort > 0;
        ShowSslFields = definition.Id == ServiceId.Nginx && definition.DefaultSslPort is > 0;

        _enabled = settings.Enabled;
        _autoStart = settings.AutoStart;
        _supervise = settings.Supervise;
        _host = settings.Host;
        _port = settings.Port;
        _sslPort = settings.SslPort ?? definition.DefaultSslPort;
        _sslEnabled = settings.SslEnabled ?? false;
        _activePackageLabel = activePackageLabel ?? "—";

        SaveCommand = new RelayCommand(_ => SaveHostAndPorts());
        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty));
    }

    public string Title { get; }
    public bool ShowNetworkFields { get; }
    public bool ShowSslFields { get; }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!SetProperty(ref _enabled, value))
            {
                return;
            }

            PersistToggle(enabled: value, autoStart: null, supervise: null);
        }
    }

    public bool AutoStart
    {
        get => _autoStart;
        set
        {
            if (!SetProperty(ref _autoStart, value))
            {
                return;
            }

            PersistToggle(enabled: null, autoStart: value, supervise: null);
        }
    }

    public bool Supervise
    {
        get => _supervise;
        set
        {
            if (!SetProperty(ref _supervise, value))
            {
                return;
            }

            PersistToggle(enabled: null, autoStart: null, supervise: value);
        }
    }

    public string Host
    {
        get => _host;
        set => SetProperty(ref _host, value);
    }

    public int Port
    {
        get => _port;
        set => SetProperty(ref _port, value);
    }

    public int? SslPort
    {
        get => _sslPort;
        set => SetProperty(ref _sslPort, value);
    }

    public bool SslEnabled
    {
        get => _sslEnabled;
        set => SetProperty(ref _sslEnabled, value);
    }

    public string ActivePackageLabel
    {
        get => _activePackageLabel;
        set => SetProperty(ref _activePackageLabel, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool ClosedAfterSave { get; private set; }
    public bool NeedsRestart { get; private set; }

    public RelayCommand SaveCommand { get; }
    public RelayCommand CloseCommand { get; }

    public event EventHandler? RequestClose;
    public event EventHandler? SettingsSaved;

    private void PersistToggle(bool? enabled, bool? autoStart, bool? supervise)
    {
        var current = _settingsStore.Load().Services[_serviceId];
        _settingsStore.UpdateService(_serviceId, current with
        {
            Enabled = enabled ?? current.Enabled,
            AutoStart = autoStart ?? current.AutoStart,
            Supervise = supervise ?? current.Supervise
        });

        StatusMessage = enabled switch
        {
            true => SessionActivityMessages.ServiceEnabled(_definition.Name, true),
            false => SessionActivityMessages.ServiceEnabled(_definition.Name, false),
            _ => autoStart switch
            {
                true => SessionActivityMessages.ServiceAutoStart(_definition.Name, true),
                false => SessionActivityMessages.ServiceAutoStart(_definition.Name, false),
                _ => supervise switch
                {
                    true => SessionActivityMessages.ServiceSupervise(_definition.Name, true),
                    false => SessionActivityMessages.ServiceSupervise(_definition.Name, false),
                    _ => string.Empty
                }
            }
        };

        SettingsSaved?.Invoke(this, EventArgs.Empty);
    }

    private void SaveHostAndPorts()
    {
        if (ShowNetworkFields && (Port < 1 || Port > 65535))
        {
            StatusMessage = "Port must be between 1 and 65535.";
            return;
        }

        if (ShowSslFields && SslPort is < 1 or > 65535)
        {
            StatusMessage = "SSL port must be between 1 and 65535.";
            return;
        }

        if (ShowNetworkFields && string.IsNullOrWhiteSpace(Host))
        {
            StatusMessage = "Host is required.";
            return;
        }

        // Warn if the chosen port is already used by another enabled service.
        if (ShowNetworkFields && Port > 0)
        {
            var allServices = _settingsStore.Load().Services;
            foreach (var (otherId, otherSettings) in allServices)
            {
                if (otherId == _serviceId) continue;
                if (!otherSettings.Enabled) continue;
                if (otherSettings.Port <= 0) continue;

                if (otherSettings.Port == Port && string.Equals(otherSettings.Host, Host.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    var otherName = SettingsDefaults.ServiceDefinitions
                        .FirstOrDefault(d => d.Id == otherId)?.Name ?? otherId.ToString();
                    var confirmed = ConfirmDialog.Show(
                        System.Windows.Application.Current?.MainWindow,
                        "Port conflict",
                        $"{_definition.Name} port {Port} is already used by {otherName}.\n\nBoth services cannot run at the same time on the same port.\n\nDo you want to save anyway?");
                    if (!confirmed)
                    {
                        return;
                    }

                    break;
                }
            }
        }

        var current = _settingsStore.Load().Services[_serviceId];
        _settingsStore.UpdateService(_serviceId, current with
        {
            Host = ShowNetworkFields ? Host.Trim() : current.Host,
            Port = ShowNetworkFields ? Port : current.Port,
            SslPort = ShowSslFields ? SslPort : current.SslPort,
            SslEnabled = ShowSslFields ? SslEnabled : current.SslEnabled
        });

        StatusMessage = SessionActivityMessages.ServiceSettingsSaved(
            _definition,
            ShowNetworkFields ? Host : current.Host,
            ShowNetworkFields ? Port : current.Port,
            ShowSslFields && SslEnabled,
            ShowSslFields ? SslPort : current.SslPort);
        ClosedAfterSave = true;
        SettingsSaved?.Invoke(this, EventArgs.Empty);
        RequestClose?.Invoke(this, EventArgs.Empty);
    }
}
