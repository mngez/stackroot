using Stackroot.App.Commands;
using Stackroot.Core.Abstractions;
using Stackroot.Core.AdminTools;
using Stackroot.Core.Settings;

namespace Stackroot.App.ViewModels;

public sealed class AdminerSettingsDialogViewModel : ViewModelBase
{
    private readonly AdminerManager _manager;
    private bool _enabled;
    private string _path = "adminer";
    private string? _selectedPhpVersionId;
    private string _previewUrl = string.Empty;
    private string _phpRequirement = string.Empty;
    private string _statusMessage = string.Empty;

    public AdminerSettingsDialogViewModel(AdminerManager manager, SettingsStore settingsStore)
    {
        _manager = manager;

        var settings = settingsStore.Load();
        var status = manager.GetStatus();
        _enabled = status.Enabled;
        _path = status.Path;
        _selectedPhpVersionId = status.PhpVersionId;
        _phpRequirement = status.PhpRequirement;
        AppDomain = string.IsNullOrWhiteSpace(settings.General.AppDomain) ? "stackroot.test" : settings.General.AppDomain.Trim();

        PhpVersions =
        [
            new CompatiblePhpOption { Id = string.Empty, Label = "Auto — compatible PHP", Compatible = true },
            .. manager.ListCompatiblePhpVersions(status.PackageId)
                .Select(v => new CompatiblePhpOption
                {
                    Id = v.Id,
                    Label = v.Compatible ? v.Label : $"{v.Label} — not supported",
                    Compatible = v.Compatible
                })
        ];

        UpdatePreviewUrl();

        SaveCommand = new RelayCommand(_ => Save());
        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty));
    }

    public string AppDomain { get; }
    public IReadOnlyList<CompatiblePhpOption> PhpVersions { get; }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string Path
    {
        get => _path;
        set
        {
            if (SetProperty(ref _path, value))
            {
                UpdatePreviewUrl();
            }
        }
    }

    public string? SelectedPhpVersionId
    {
        get => _selectedPhpVersionId;
        set => SetProperty(ref _selectedPhpVersionId, string.IsNullOrWhiteSpace(value) ? null : value);
    }

    public string PreviewUrl
    {
        get => _previewUrl;
        private set => SetProperty(ref _previewUrl, value);
    }

    public string PhpRequirement
    {
        get => _phpRequirement;
        private set => SetProperty(ref _phpRequirement, value);
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
        if (string.IsNullOrWhiteSpace(Path))
        {
            StatusMessage = "URL path is required.";
            return;
        }

        _manager.ApplyConfig(new AdminerConfigUpdate
        {
            Enabled = Enabled,
            BaseDomain = AppDomain,
            Path = Path.Trim('/'),
            PhpVersionId = string.IsNullOrWhiteSpace(SelectedPhpVersionId) ? null : SelectedPhpVersionId
        });

        StatusMessage = "Settings saved.";
        SettingsSaved?.Invoke(this, EventArgs.Empty);
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private void UpdatePreviewUrl()
    {
        var segment = string.IsNullOrWhiteSpace(Path) ? "adminer" : Path.Trim('/');
        PreviewUrl = $"http://{AppDomain}/{segment}/";
    }
}
