using Stackroot.App.Commands;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Settings;

namespace Stackroot.App.ViewModels;

public sealed class NodeSettingsDialogViewModel : ViewModelBase
{
    private readonly SettingsStore _settingsStore;
    private string _npmRegistry = "https://registry.npmjs.org/";
    private bool _autoUseNvmrc = true;
    private string _statusMessage = string.Empty;

    public NodeSettingsDialogViewModel(SettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
        var settings = settingsStore.Load();
        _npmRegistry = settings.Node.NpmRegistry;
        _autoUseNvmrc = settings.Node.AutoUseNvmrc;

        SaveCommand = new RelayCommand(_ => Save());
        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty));
    }

    public string NpmRegistry
    {
        get => _npmRegistry;
        set => SetProperty(ref _npmRegistry, value);
    }

    public bool AutoUseNvmrc
    {
        get => _autoUseNvmrc;
        set => SetProperty(ref _autoUseNvmrc, value);
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
        if (string.IsNullOrWhiteSpace(NpmRegistry))
        {
            StatusMessage = "npm registry URL is required.";
            return;
        }

        if (!Uri.TryCreate(NpmRegistry.Trim(), UriKind.Absolute, out _))
        {
            StatusMessage = "Enter a valid registry URL.";
            return;
        }

        _settingsStore.UpdateNode(new NodeSettings
        {
            NpmRegistry = NpmRegistry.Trim(),
            AutoUseNvmrc = AutoUseNvmrc
        });

        StatusMessage = "Node settings saved.";
        SettingsSaved?.Invoke(this, EventArgs.Empty);
        RequestClose?.Invoke(this, EventArgs.Empty);
    }
}
