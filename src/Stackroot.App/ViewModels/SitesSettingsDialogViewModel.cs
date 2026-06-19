using Stackroot.App.Commands;
using Stackroot.App.Services;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Settings;

namespace Stackroot.App.ViewModels;

public sealed class SitesSettingsDialogViewModel : ViewModelBase
{
    private readonly SettingsStore _settingsStore;
    private readonly TestDnsCoordinator? _testDns;
    private bool _autoHosts;
    private bool _testDnsEnabled;
    private string _statusMessage = string.Empty;

    public SitesSettingsDialogViewModel(SettingsStore settingsStore, TestDnsCoordinator? testDns = null)
    {
        _settingsStore = settingsStore;
        _testDns = testDns;
        var settings = settingsStore.Load();
        _autoHosts = settings.Sites.AutoHosts;
        _testDnsEnabled = settings.Sites.TestDnsEnabled;

        SaveCommand = new RelayCommand(_ => _ = SaveAsync());
        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty));
    }

    public bool AutoHosts
    {
        get => _autoHosts;
        set => SetProperty(ref _autoHosts, value);
    }

    public bool TestDnsEnabled
    {
        get => _testDnsEnabled;
        set => SetProperty(ref _testDnsEnabled, value);
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

    private async Task SaveAsync()
    {
        _settingsStore.UpdateSites(new SiteDefaults
        {
            AutoHosts = AutoHosts,
            TestDnsEnabled = TestDnsEnabled
        });

        if (_testDns is not null)
        {
            try
            {
                await _testDns.ApplySettingsAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
                SettingsSaved?.Invoke(this, EventArgs.Empty);
                RequestClose?.Invoke(this, EventArgs.Empty);
                return;
            }
        }

        StatusMessage = BuildStatusMessage();
        SettingsSaved?.Invoke(this, EventArgs.Empty);
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private string BuildStatusMessage()
    {
        if (TestDnsEnabled)
        {
            return "Local .test DNS enabled. Wildcard subdomains resolve via 127.0.0.1; other domains are unchanged.";
        }

        return AutoHosts
            ? "Auto-hosts enabled. Stackroot will update your hosts file for enabled domains."
            : "Auto-hosts disabled. Add domains to your hosts file manually.";
    }
}
