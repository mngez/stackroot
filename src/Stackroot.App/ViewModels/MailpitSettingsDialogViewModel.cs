using Stackroot.App.Commands;
using Stackroot.Core.Abstractions;
using Stackroot.Core.AdminTools;
using Stackroot.Core.Settings;

namespace Stackroot.App.ViewModels;

public sealed class MailpitSettingsDialogViewModel : ViewModelBase
{
    private readonly SettingsStore _settingsStore;
    private bool _enabled = true;
    private int _smtpPort = 1025;
    private int _webPort = 8025;
    private bool _autoStart = true;
    private bool _supervise = true;
    private string _statusMessage = string.Empty;

    public MailpitSettingsDialogViewModel(SettingsStore settingsStore)
    {
        _settingsStore = settingsStore;

        var settings = settingsStore.Load();
        _enabled = settings.Mailpit.Enabled;
        _smtpPort = settings.Mailpit.SmtpPort;
        _webPort = settings.Mailpit.WebPort;
        _autoStart = settings.Mailpit.AutoStart;
        _supervise = settings.Mailpit.Supervise;
        AppDomain = string.IsNullOrWhiteSpace(settings.General.AppDomain) ? "stackroot.test" : settings.General.AppDomain.Trim();
        InboxUrl = $"http://{AppDomain}/{MailpitManager.WebPath}/";

        SaveCommand = new RelayCommand(_ => Save());
        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty));
    }

    public string AppDomain { get; }
    public string InboxUrl { get; }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public int SmtpPort
    {
        get => _smtpPort;
        set => SetProperty(ref _smtpPort, value);
    }

    public int WebPort
    {
        get => _webPort;
        set => SetProperty(ref _webPort, value);
    }

    public bool AutoStart
    {
        get => _autoStart;
        set => SetProperty(ref _autoStart, value);
    }

    public bool Supervise
    {
        get => _supervise;
        set => SetProperty(ref _supervise, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public RelayCommand SaveCommand { get; }
    public RelayCommand CloseCommand { get; }

    public event EventHandler? RequestClose;
    public event EventHandler? SettingsSaved;

    private void Save()
    {
        _settingsStore.UpdateMailpit(new MailpitSettings
        {
            Enabled = Enabled,
            SmtpPort = SmtpPort <= 0 ? 1025 : SmtpPort,
            WebPort = WebPort <= 0 ? 8025 : WebPort,
            AutoStart = AutoStart,
            Supervise = Supervise
        });

        StatusMessage = "Mailpit settings saved.";
        SettingsSaved?.Invoke(this, EventArgs.Empty);
        RequestClose?.Invoke(this, EventArgs.Empty);
    }
}
