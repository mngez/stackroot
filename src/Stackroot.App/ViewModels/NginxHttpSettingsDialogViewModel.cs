using System.Collections.ObjectModel;
using System.IO;
using Stackroot.App.Commands;
using Stackroot.App.Helpers;
using Stackroot.Core.Abstractions;
using Stackroot.Core.IO;
using Stackroot.Core.Nginx;
using Stackroot.Core.Settings;

namespace Stackroot.App.ViewModels;

public sealed class NginxHttpSettingsDialogViewModel : ViewModelBase
{
    private readonly SettingsStore _settingsStore;
    private readonly string _mainConfigPath;
    private readonly Func<CancellationToken, Task>? _reloadNginxAsync;
    private bool _manageMainConfigManually;
    private string _workerProcesses = "auto";
    private int _workerConnections = 1024;
    private bool _multiAccept = true;
    private int _keepaliveTimeout = 65;
    private bool _sendfile = true;
    private bool _tcpNopush = true;
    private string _clientMaxBodySize = "512M";
    private int _typesHashMaxSize = 2048;
    private int _serverNamesHashBucketSize = 128;
    private bool _gzipEnabled = true;
    private int _gzipCompLevel = 5;
    private int _gzipMinLength = 256;
    private bool _accessLogEnabled = true;
    private string _errorLogLevel = "warn";
    private int _fastCgiConnectTimeoutSeconds = 60;
    private int _fastCgiSendTimeoutSeconds = 600;
    private int _fastCgiReadTimeoutSeconds = 600;
    private int _proxyConnectTimeoutSeconds = 60;
    private int _proxySendTimeoutSeconds = 600;
    private int _proxyReadTimeoutSeconds = 600;
    private bool _isSaving;
    private string _statusMessage = string.Empty;
    private string _saveButtonText = "Save";

    public NginxHttpSettingsDialogViewModel(
        SettingsStore settingsStore,
        StackrootPaths paths,
        Func<CancellationToken, Task>? reloadNginxAsync = null)
    {
        _settingsStore = settingsStore;
        _mainConfigPath = NginxRuntime.MainConfigPath(paths);
        _reloadNginxAsync = reloadNginxAsync;

        ErrorLogLevels = new ObservableCollection<string>(["warn", "notice", "info", "error", "debug"]);

        ApplyToFields(NginxHttpSettingsSanitizer.Sanitize(settingsStore.Load().NginxHttp));

        SaveCommand = new RelayCommand(_ => _ = SaveAsync(), _ => !IsSaving);
        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty), _ => !IsSaving);
        ResetDefaultsCommand = new RelayCommand(_ => ResetDefaults(), _ => !IsSaving && IsAutoTuningEnabled);
        OpenMainConfigCommand = new RelayCommand(_ => OpenMainConfig(), _ => !IsSaving);
    }

    public ObservableCollection<string> ErrorLogLevels { get; }

    public string MainConfigPath => _mainConfigPath;

    public bool IsAutoTuningEnabled => !ManageMainConfigManually;

    public bool ManageMainConfigManually
    {
        get => _manageMainConfigManually;
        set
        {
            if (SetProperty(ref _manageMainConfigManually, value))
            {
                RaisePropertyChanged(nameof(IsAutoTuningEnabled));
                ResetDefaultsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string WorkerProcesses
    {
        get => _workerProcesses;
        set => SetProperty(ref _workerProcesses, value);
    }

    public int WorkerConnections
    {
        get => _workerConnections;
        set => SetProperty(ref _workerConnections, value);
    }

    public bool MultiAccept
    {
        get => _multiAccept;
        set => SetProperty(ref _multiAccept, value);
    }

    public int KeepaliveTimeout
    {
        get => _keepaliveTimeout;
        set => SetProperty(ref _keepaliveTimeout, value);
    }

    public bool Sendfile
    {
        get => _sendfile;
        set => SetProperty(ref _sendfile, value);
    }

    public bool TcpNopush
    {
        get => _tcpNopush;
        set => SetProperty(ref _tcpNopush, value);
    }

    public string ClientMaxBodySize
    {
        get => _clientMaxBodySize;
        set => SetProperty(ref _clientMaxBodySize, value);
    }

    public int TypesHashMaxSize
    {
        get => _typesHashMaxSize;
        set => SetProperty(ref _typesHashMaxSize, value);
    }

    public int ServerNamesHashBucketSize
    {
        get => _serverNamesHashBucketSize;
        set => SetProperty(ref _serverNamesHashBucketSize, value);
    }

    public bool GzipEnabled
    {
        get => _gzipEnabled;
        set => SetProperty(ref _gzipEnabled, value);
    }

    public int GzipCompLevel
    {
        get => _gzipCompLevel;
        set => SetProperty(ref _gzipCompLevel, value);
    }

    public int GzipMinLength
    {
        get => _gzipMinLength;
        set => SetProperty(ref _gzipMinLength, value);
    }

    public bool AccessLogEnabled
    {
        get => _accessLogEnabled;
        set => SetProperty(ref _accessLogEnabled, value);
    }

    public string ErrorLogLevel
    {
        get => _errorLogLevel;
        set => SetProperty(ref _errorLogLevel, value);
    }

    public int FastCgiConnectTimeoutSeconds
    {
        get => _fastCgiConnectTimeoutSeconds;
        set => SetProperty(ref _fastCgiConnectTimeoutSeconds, value);
    }

    public int FastCgiSendTimeoutSeconds
    {
        get => _fastCgiSendTimeoutSeconds;
        set => SetProperty(ref _fastCgiSendTimeoutSeconds, value);
    }

    public int FastCgiReadTimeoutSeconds
    {
        get => _fastCgiReadTimeoutSeconds;
        set => SetProperty(ref _fastCgiReadTimeoutSeconds, value);
    }

    public int ProxyConnectTimeoutSeconds
    {
        get => _proxyConnectTimeoutSeconds;
        set => SetProperty(ref _proxyConnectTimeoutSeconds, value);
    }

    public int ProxySendTimeoutSeconds
    {
        get => _proxySendTimeoutSeconds;
        set => SetProperty(ref _proxySendTimeoutSeconds, value);
    }

    public int ProxyReadTimeoutSeconds
    {
        get => _proxyReadTimeoutSeconds;
        set => SetProperty(ref _proxyReadTimeoutSeconds, value);
    }

    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (SetProperty(ref _isSaving, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
                CloseCommand.RaiseCanExecuteChanged();
                ResetDefaultsCommand.RaiseCanExecuteChanged();
                OpenMainConfigCommand.RaiseCanExecuteChanged();
                RaisePropertyChanged(nameof(IsIdle));
            }
        }
    }

    public bool IsIdle => !IsSaving;

    public string SaveButtonText
    {
        get => _saveButtonText;
        private set => SetProperty(ref _saveButtonText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public RelayCommand SaveCommand { get; }
    public RelayCommand CloseCommand { get; }
    public RelayCommand ResetDefaultsCommand { get; }
    public RelayCommand OpenMainConfigCommand { get; }

    public event EventHandler? RequestClose;
    public event EventHandler? SettingsSaved;

    private void OpenMainConfig()
    {
        if (!File.Exists(_mainConfigPath))
        {
            StatusMessage = "nginx.conf does not exist yet. Save automatic settings once or create the file manually.";
            return;
        }

        StackrootLogViewer.OpenInPreferredEditor(_mainConfigPath, _settingsStore);
    }

    private void ResetDefaults()
    {
        var manual = ManageMainConfigManually;
        ApplyToFields(new NginxHttpSettings());
        ManageMainConfigManually = manual;
        StatusMessage = "Restored recommended defaults. Click Save to apply.";
    }

    private void ApplyToFields(NginxHttpSettings settings)
    {
        ManageMainConfigManually = settings.ManageMainConfigManually;
        WorkerProcesses = settings.WorkerProcesses;
        WorkerConnections = settings.WorkerConnections;
        MultiAccept = settings.MultiAccept;
        KeepaliveTimeout = settings.KeepaliveTimeout;
        Sendfile = settings.Sendfile;
        TcpNopush = settings.TcpNopush;
        ClientMaxBodySize = settings.ClientMaxBodySize;
        TypesHashMaxSize = settings.TypesHashMaxSize;
        ServerNamesHashBucketSize = settings.ServerNamesHashBucketSize;
        GzipEnabled = settings.GzipEnabled;
        GzipCompLevel = settings.GzipCompLevel;
        GzipMinLength = settings.GzipMinLength;
        AccessLogEnabled = settings.AccessLogEnabled;
        ErrorLogLevel = settings.ErrorLogLevel;
        FastCgiConnectTimeoutSeconds = settings.FastCgiConnectTimeoutSeconds;
        FastCgiSendTimeoutSeconds = settings.FastCgiSendTimeoutSeconds;
        FastCgiReadTimeoutSeconds = settings.FastCgiReadTimeoutSeconds;
        ProxyConnectTimeoutSeconds = settings.ProxyConnectTimeoutSeconds;
        ProxySendTimeoutSeconds = settings.ProxySendTimeoutSeconds;
        ProxyReadTimeoutSeconds = settings.ProxyReadTimeoutSeconds;
    }

    private NginxHttpSettings BuildPatch() => new()
    {
        ManageMainConfigManually = ManageMainConfigManually,
        WorkerProcesses = WorkerProcesses,
        WorkerConnections = WorkerConnections,
        MultiAccept = MultiAccept,
        KeepaliveTimeout = KeepaliveTimeout,
        Sendfile = Sendfile,
        TcpNopush = TcpNopush,
        ClientMaxBodySize = ClientMaxBodySize,
        TypesHashMaxSize = TypesHashMaxSize,
        ServerNamesHashBucketSize = ServerNamesHashBucketSize,
        GzipEnabled = GzipEnabled,
        GzipCompLevel = GzipCompLevel,
        GzipMinLength = GzipMinLength,
        AccessLogEnabled = AccessLogEnabled,
        ErrorLogLevel = ErrorLogLevel,
        FastCgiConnectTimeoutSeconds = FastCgiConnectTimeoutSeconds,
        FastCgiSendTimeoutSeconds = FastCgiSendTimeoutSeconds,
        FastCgiReadTimeoutSeconds = FastCgiReadTimeoutSeconds,
        ProxyConnectTimeoutSeconds = ProxyConnectTimeoutSeconds,
        ProxySendTimeoutSeconds = ProxySendTimeoutSeconds,
        ProxyReadTimeoutSeconds = ProxyReadTimeoutSeconds
    };

    private async Task SaveAsync()
    {
        if (IsSaving)
        {
            return;
        }

        IsSaving = true;
        SaveButtonText = "Saving…";
        StatusMessage = ManageMainConfigManually
            ? "Saving settings…"
            : "Updating nginx configuration…";
        var previous = _settingsStore.Load().NginxHttp;

        try
        {
            var sanitized = NginxHttpSettingsSanitizer.Sanitize(BuildPatch());
            _settingsStore.UpdateNginxHttp(sanitized);
            ApplyToFields(sanitized);

            if (_reloadNginxAsync is not null)
            {
                await _reloadNginxAsync(CancellationToken.None).ConfigureAwait(true);
            }

            StatusMessage = sanitized.ManageMainConfigManually
                ? "Settings saved. Main nginx.conf is managed manually — edit the file, then reload nginx."
                : "Nginx HTTP settings saved.";
            SettingsSaved?.Invoke(this, EventArgs.Empty);
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            try
            {
                _settingsStore.UpdateNginxHttp(previous);
                ApplyToFields(previous);
            }
            catch
            {
                // Keep the original error visible.
            }

            StatusMessage = $"Could not apply nginx settings. Changes were reverted. {ex.Message}";
        }
        finally
        {
            IsSaving = false;
            SaveButtonText = "Save";
        }
    }
}
