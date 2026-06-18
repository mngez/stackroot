using System.Diagnostics;
using System.IO;
using Stackroot.App.Commands;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Services.Php;
using Stackroot.Core.Settings;

namespace Stackroot.App.ViewModels;

public sealed class PhpVersionSettingsDialogViewModel : ViewModelBase
{
    private readonly PhpExtensionManager _extensionManager;
    private readonly PhpConfigWriter _configWriter;
    private readonly SettingsStore _settingsStore;
    private readonly string _versionId;
    private string _memoryLimit = "256M";
    private string _maxExecutionTime = "120";
    private string _uploadMaxFilesize = "64M";
    private string _postMaxSize = "64M";
    private bool _displayErrors = true;
    private bool _logErrors = true;
    private bool _hideWarnings;
    private bool _hideDeprecated = true;
    private string _statusMessage = string.Empty;
    private string _iniPath = string.Empty;

    public PhpVersionSettingsDialogViewModel(
        PhpExtensionManager extensionManager,
        PhpConfigWriter configWriter,
        SettingsStore settingsStore,
        string versionId,
        string versionLabel)
    {
        _extensionManager = extensionManager;
        _configWriter = configWriter;
        _settingsStore = settingsStore;
        _versionId = versionId;
        VersionLabel = versionLabel;

        var versionSettings = _extensionManager.EnsureVersionSettings(versionId);
        _memoryLimit = versionSettings.MemoryLimit;
        _maxExecutionTime = versionSettings.MaxExecutionTime;
        _uploadMaxFilesize = versionSettings.UploadMaxFilesize;
        _postMaxSize = versionSettings.PostMaxSize;
        _displayErrors = versionSettings.DisplayErrors != false;
        _logErrors = versionSettings.LogErrors != false;
        _hideWarnings = versionSettings.HideWarnings == true;
        _hideDeprecated = versionSettings.HideDeprecated != false;

        var settings = settingsStore.Load();
        _iniPath = _configWriter.WritePhpConfig(settings, versionId) ?? string.Empty;

        SaveCommand = new RelayCommand(_ => Save());
        OpenIniCommand = new RelayCommand(_ => OpenIni());
        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty));
    }

    public string VersionLabel { get; }
    public string VersionId => _versionId;
    public string IniPath => _iniPath;

    public string MemoryLimit
    {
        get => _memoryLimit;
        set => SetProperty(ref _memoryLimit, value);
    }

    public string MaxExecutionTime
    {
        get => _maxExecutionTime;
        set => SetProperty(ref _maxExecutionTime, value);
    }

    public string UploadMaxFilesize
    {
        get => _uploadMaxFilesize;
        set => SetProperty(ref _uploadMaxFilesize, value);
    }

    public string PostMaxSize
    {
        get => _postMaxSize;
        set => SetProperty(ref _postMaxSize, value);
    }

    public bool DisplayErrors
    {
        get => _displayErrors;
        set => SetProperty(ref _displayErrors, value);
    }

    public bool LogErrors
    {
        get => _logErrors;
        set => SetProperty(ref _logErrors, value);
    }

    public bool HideWarnings
    {
        get => _hideWarnings;
        set => SetProperty(ref _hideWarnings, value);
    }

    public bool HideDeprecated
    {
        get => _hideDeprecated;
        set => SetProperty(ref _hideDeprecated, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public RelayCommand SaveCommand { get; }
    public RelayCommand OpenIniCommand { get; }
    public RelayCommand CloseCommand { get; }

    public event EventHandler? RequestClose;
    public event EventHandler? SettingsSaved;

    private void Save()
    {
        _extensionManager.SaveVersionSettings(_versionId, new PhpVersionSettings
        {
            MemoryLimit = MemoryLimit.Trim(),
            MaxExecutionTime = MaxExecutionTime.Trim(),
            UploadMaxFilesize = UploadMaxFilesize.Trim(),
            PostMaxSize = PostMaxSize.Trim(),
            DisplayErrors = DisplayErrors,
            LogErrors = LogErrors,
            HideWarnings = HideWarnings,
            HideDeprecated = HideDeprecated
        });

        var settings = _settingsStore.Load();
        _iniPath = _configWriter.WritePhpConfig(settings, _versionId) ?? string.Empty;
        RaisePropertyChanged(nameof(IniPath));
        StatusMessage = "Settings saved.";
        SettingsSaved?.Invoke(this, EventArgs.Empty);
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private void OpenIni()
    {
        if (string.IsNullOrWhiteSpace(_iniPath) || !File.Exists(_iniPath))
        {
            StatusMessage = "Generated ini file is not available yet.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _iniPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }
}
