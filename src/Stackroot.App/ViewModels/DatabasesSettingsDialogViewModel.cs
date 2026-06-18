using Stackroot.App.Commands;
using Stackroot.App.Helpers;
using Stackroot.Core.Abstractions;
using Stackroot.Core.AdminTools;
using Stackroot.Core.Observability;
using Stackroot.Core.Services;
using Stackroot.Core.Settings;

namespace Stackroot.App.ViewModels;

public sealed class DatabasesSettingsDialogViewModel : ViewModelBase
{
    private readonly SettingsStore _settingsStore;
    private readonly ServiceManager _serviceManager;
    private readonly PhpMyAdminManager _phpMyAdminManager;
    private readonly AppDomainConfigWriter _appDomainConfigWriter;
    private readonly IDiagnosticsReporter _diagnostics;
    private string _mysqlUsername = "root";
    private string _mysqlPassword = "root";
    private string _mariadbUsername = "root";
    private string _mariadbPassword = "root";
    private string _statusMessage = string.Empty;

    public DatabasesSettingsDialogViewModel(
        SettingsStore settingsStore,
        ServiceManager serviceManager,
        PhpMyAdminManager phpMyAdminManager,
        AppDomainConfigWriter appDomainConfigWriter,
        IDiagnosticsReporter diagnostics)
    {
        _settingsStore = settingsStore;
        _serviceManager = serviceManager;
        _phpMyAdminManager = phpMyAdminManager;
        _appDomainConfigWriter = appDomainConfigWriter;
        _diagnostics = diagnostics;

        var settings = settingsStore.Load();
        _mysqlUsername = settings.Databases.Mysql.Username;
        _mysqlPassword = settings.Databases.Mysql.Password;
        _mariadbUsername = settings.Databases.Mariadb.Username;
        _mariadbPassword = settings.Databases.Mariadb.Password;

        SaveCommand = new RelayCommand(_ => Save());
        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty));
    }

    public string MysqlUsername
    {
        get => _mysqlUsername;
        set => SetProperty(ref _mysqlUsername, value);
    }

    public string MysqlPassword
    {
        get => _mysqlPassword;
        set => SetProperty(ref _mysqlPassword, value);
    }

    public string MariadbUsername
    {
        get => _mariadbUsername;
        set => SetProperty(ref _mariadbUsername, value);
    }

    public string MariadbPassword
    {
        get => _mariadbPassword;
        set => SetProperty(ref _mariadbPassword, value);
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
        _settingsStore.UpdateDatabases(new DatabaseSettings
        {
            Mysql = new DatabaseCredentials
            {
                Username = string.IsNullOrWhiteSpace(MysqlUsername) ? "root" : MysqlUsername.Trim(),
                Password = MysqlPassword ?? string.Empty
            },
            Mariadb = new DatabaseCredentials
            {
                Username = string.IsNullOrWhiteSpace(MariadbUsername) ? "root" : MariadbUsername.Trim(),
                Password = MariadbPassword ?? string.Empty
            }
        });

        SettingsSaved?.Invoke(this, EventArgs.Empty);
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    public async Task ApplySavedChangesAsync()
    {
        var result = await UiBackgroundAction.RunAsync(
            _diagnostics,
            "Databases",
            "Save database credentials",
            async () =>
            {
                await _serviceManager.SyncEnabledSqlCredentialsAsync().ConfigureAwait(false);
                await _phpMyAdminManager.ApplyAsync().ConfigureAwait(false);
                _appDomainConfigWriter.Write();
            },
            _ => { },
            onError: ex => throw new InvalidOperationException(ex.Message, ex),
            busyMessage: "Applying credentials and repairing SQL privileges…").ConfigureAwait(true);

        if (!result)
        {
            throw new InvalidOperationException("Failed to apply database credentials.");
        }
    }
}
