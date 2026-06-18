using Stackroot.App.Commands;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Settings;

namespace Stackroot.App.ViewModels;

public sealed class SitesSettingsDialogViewModel : ViewModelBase
{
    private readonly SettingsStore _settingsStore;
    private bool _autoHosts;
    private string _statusMessage = string.Empty;

    public SitesSettingsDialogViewModel(SettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
        var settings = settingsStore.Load();
        _autoHosts = settings.Sites.AutoHosts;

        SaveCommand = new RelayCommand(_ => Save());
        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty));
    }

    public bool AutoHosts
    {
        get => _autoHosts;
        set => SetProperty(ref _autoHosts, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public RelayCommand SaveCommand { get; }
    public RelayCommand CloseCommand { get; }

    public event EventHandler? RequestClose;
    public event EventHandler? SettingsSaved;

    private void Save()
    {
        _settingsStore.UpdateSites(new SiteDefaults { AutoHosts = AutoHosts });
        StatusMessage = AutoHosts
            ? "Auto-hosts enabled. Stackroot will update your hosts file for enabled domains."
            : "Auto-hosts disabled. Add domains to your hosts file manually.";
        SettingsSaved?.Invoke(this, EventArgs.Empty);
        RequestClose?.Invoke(this, EventArgs.Empty);
    }
}
