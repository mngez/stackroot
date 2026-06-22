using System.IO;
using Stackroot.App.Commands;
using Stackroot.App.Helpers;
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
    private bool _manageIniManually;
    private string _memoryLimit = "-1";
    private string _maxExecutionTime = "0";
    private string _uploadMaxFilesize = "512M";
    private string _postMaxSize = "512M";
    private int _maxInputTime = 600;
    private int _maxInputVars = 5000;
    private int _defaultSocketTimeout = 300;
    private string _realpathCacheSize = "4096K";
    private int _realpathCacheTtl = 600;
    private bool _opcacheEnabled = true;
    private bool _opcacheEnableCli = true;
    private bool _opcacheValidateTimestamps = true;
    private int _opcacheRevalidateFreq;
    private int _opcacheMemoryConsumption = 256;
    private int _opcacheMaxAcceleratedFiles = 20_000;
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

        ApplyToFields(PhpVersionSettingsSanitizer.Sanitize(_extensionManager.EnsureVersionSettings(versionId)));

        var settings = settingsStore.Load();
        _iniPath = _configWriter.WritePhpConfig(settings, versionId) ?? string.Empty;

        SaveCommand = new RelayCommand(_ => Save());
        ResetDefaultsCommand = new RelayCommand(_ => ResetDefaults(), _ => IsAutoTuningEnabled);
        OpenIniCommand = new RelayCommand(_ => OpenIni());
        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty));
    }

    public string VersionLabel { get; }
    public string VersionId => _versionId;
    public string IniPath => _iniPath;
    public bool IsAutoTuningEnabled => !ManageIniManually;

    public bool ManageIniManually
    {
        get => _manageIniManually;
        set
        {
            if (SetProperty(ref _manageIniManually, value))
            {
                RaisePropertyChanged(nameof(IsAutoTuningEnabled));
                ResetDefaultsCommand.RaiseCanExecuteChanged();
            }
        }
    }

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

    public int MaxInputTime
    {
        get => _maxInputTime;
        set => SetProperty(ref _maxInputTime, value);
    }

    public int MaxInputVars
    {
        get => _maxInputVars;
        set => SetProperty(ref _maxInputVars, value);
    }

    public int DefaultSocketTimeout
    {
        get => _defaultSocketTimeout;
        set => SetProperty(ref _defaultSocketTimeout, value);
    }

    public string RealpathCacheSize
    {
        get => _realpathCacheSize;
        set => SetProperty(ref _realpathCacheSize, value);
    }

    public int RealpathCacheTtl
    {
        get => _realpathCacheTtl;
        set => SetProperty(ref _realpathCacheTtl, value);
    }

    public bool OpcacheEnabled
    {
        get => _opcacheEnabled;
        set => SetProperty(ref _opcacheEnabled, value);
    }

    public bool OpcacheEnableCli
    {
        get => _opcacheEnableCli;
        set => SetProperty(ref _opcacheEnableCli, value);
    }

    public bool OpcacheValidateTimestamps
    {
        get => _opcacheValidateTimestamps;
        set => SetProperty(ref _opcacheValidateTimestamps, value);
    }

    public int OpcacheRevalidateFreq
    {
        get => _opcacheRevalidateFreq;
        set => SetProperty(ref _opcacheRevalidateFreq, value);
    }

    public int OpcacheMemoryConsumption
    {
        get => _opcacheMemoryConsumption;
        set => SetProperty(ref _opcacheMemoryConsumption, value);
    }

    public int OpcacheMaxAcceleratedFiles
    {
        get => _opcacheMaxAcceleratedFiles;
        set => SetProperty(ref _opcacheMaxAcceleratedFiles, value);
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
    public RelayCommand ResetDefaultsCommand { get; }
    public RelayCommand OpenIniCommand { get; }
    public RelayCommand CloseCommand { get; }

    public event EventHandler? RequestClose;
    public event EventHandler? SettingsSaved;

    private void ResetDefaults()
    {
        var manual = ManageIniManually;
        ApplyToFields(new PhpVersionSettings());
        ManageIniManually = manual;
        StatusMessage = "Restored recommended defaults. Click Save to apply.";
    }

    private void ApplyToFields(PhpVersionSettings settings)
    {
        ManageIniManually = settings.ManageIniManually;
        MemoryLimit = settings.MemoryLimit;
        MaxExecutionTime = settings.MaxExecutionTime;
        UploadMaxFilesize = settings.UploadMaxFilesize;
        PostMaxSize = settings.PostMaxSize;
        MaxInputTime = settings.MaxInputTime;
        MaxInputVars = settings.MaxInputVars;
        DefaultSocketTimeout = settings.DefaultSocketTimeout;
        RealpathCacheSize = settings.RealpathCacheSize;
        RealpathCacheTtl = settings.RealpathCacheTtl;
        OpcacheEnabled = settings.OpcacheEnabled;
        OpcacheEnableCli = settings.OpcacheEnableCli;
        OpcacheValidateTimestamps = settings.OpcacheValidateTimestamps;
        OpcacheRevalidateFreq = settings.OpcacheRevalidateFreq;
        OpcacheMemoryConsumption = settings.OpcacheMemoryConsumption;
        OpcacheMaxAcceleratedFiles = settings.OpcacheMaxAcceleratedFiles;
        DisplayErrors = settings.DisplayErrors != false;
        LogErrors = settings.LogErrors != false;
        HideWarnings = settings.HideWarnings == true;
        HideDeprecated = settings.HideDeprecated != false;
    }

    private PhpVersionSettings BuildPatch() => new()
    {
        ManageIniManually = ManageIniManually,
        MemoryLimit = MemoryLimit,
        MaxExecutionTime = MaxExecutionTime,
        UploadMaxFilesize = UploadMaxFilesize,
        PostMaxSize = PostMaxSize,
        MaxInputTime = MaxInputTime,
        MaxInputVars = MaxInputVars,
        DefaultSocketTimeout = DefaultSocketTimeout,
        RealpathCacheSize = RealpathCacheSize,
        RealpathCacheTtl = RealpathCacheTtl,
        OpcacheEnabled = OpcacheEnabled,
        OpcacheEnableCli = OpcacheEnableCli,
        OpcacheValidateTimestamps = OpcacheValidateTimestamps,
        OpcacheRevalidateFreq = OpcacheRevalidateFreq,
        OpcacheMemoryConsumption = OpcacheMemoryConsumption,
        OpcacheMaxAcceleratedFiles = OpcacheMaxAcceleratedFiles,
        DisplayErrors = DisplayErrors,
        LogErrors = LogErrors,
        HideWarnings = HideWarnings,
        HideDeprecated = HideDeprecated
    };

    private void Save()
    {
        var sanitized = PhpVersionSettingsSanitizer.Sanitize(BuildPatch());
        _extensionManager.SaveVersionSettings(_versionId, sanitized);
        ApplyToFields(sanitized);

        var settings = _settingsStore.Load();
        _iniPath = _configWriter.WritePhpConfig(settings, _versionId) ?? string.Empty;
        RaisePropertyChanged(nameof(IniPath));
        StatusMessage = sanitized.ManageIniManually
            ? "Settings saved. php.ini is managed manually — edit the file, then restart PHP."
            : "Settings saved.";
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
            StackrootLogViewer.OpenInPreferredEditor(_iniPath, _settingsStore);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
    }
}
