using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Stackroot.App.Commands;
using Stackroot.App.Helpers;
using Stackroot.App.Services;
using Stackroot.App.Views;
using Stackroot.Core.Abstractions;
using Stackroot.Core.AdminTools;
using Stackroot.Core.Catalog;
using Stackroot.Core.Databases;
using Stackroot.Core.Databases.Models;
using Stackroot.Core.Observability;
using Stackroot.Core.Services;
using Stackroot.Core.Settings;
using Stackroot.Core.Sites.Management;

namespace Stackroot.App.ViewModels;

public sealed class DatabasesViewModel : ViewModelBase
{
    private readonly DatabaseManager _databaseManager;
    private readonly SettingsStore _settingsStore;
    private readonly SiteManager _siteManager;
    private readonly ServiceManager _serviceManager;
    private readonly PhpMyAdminManager _phpMyAdminManager;
    private readonly AppDomainConfigWriter _appDomainConfigWriter;
    private readonly IDiagnosticsReporter _diagnostics;
    private readonly SessionActivityReporter _activity;
    private readonly PackageCatalogStore _catalogStore;
    private readonly PackageInstallCoordinator _packages;
    private string _filterText = string.Empty;
    private DatabaseEngineFilterOption _selectedEngineFilter = new(null, "All engines");
    private string? _lastMessage;
    private DatabaseRecord? _selectedDatabase;
    private string _envSnippet = string.Empty;
    private bool _isBusy;
    private string _busyMessage = string.Empty;

    public DatabasesViewModel(
        DatabaseManager databaseManager,
        SettingsStore settingsStore,
        SiteManager siteManager,
        ServiceManager serviceManager,
        PhpMyAdminManager phpMyAdminManager,
        AppDomainConfigWriter appDomainConfigWriter,
        IDiagnosticsReporter diagnostics,
        SessionActivityReporter activity,
        PackageCatalogStore catalogStore,
        PackageInstallCoordinator packages)
    {
        _databaseManager = databaseManager;
        _settingsStore = settingsStore;
        _siteManager = siteManager;
        _serviceManager = serviceManager;
        _phpMyAdminManager = phpMyAdminManager;
        _appDomainConfigWriter = appDomainConfigWriter;
        _diagnostics = diagnostics;
        _activity = activity;
        _catalogStore = catalogStore;
        _packages = packages;

        Databases = [];
        EngineFilters =
        [
            new DatabaseEngineFilterOption(null, "All engines"),
            new DatabaseEngineFilterOption(SqlEngine.Mysql, "MySQL"),
            new DatabaseEngineFilterOption(SqlEngine.Mariadb, "MariaDB"),
            new DatabaseEngineFilterOption(SqlEngine.Postgresql, "PostgreSQL"),
            new DatabaseEngineFilterOption(SqlEngine.Mongodb, "MongoDB")
        ];

        RefreshCommand = new RelayCommand(_ => Reload(), _ => !IsBusy);
        OpenCreateDialogCommand = new RelayCommand(_ => OpenCreateDialog(), _ => !IsBusy);
        BackupCommand = new RelayCommand(name => _ = BackupAsync(name as string), _ => !IsBusy);
        DeleteCommand = new RelayCommand(name => _ = DeleteAsync(name as string), _ => !IsBusy);
        OpenSettingsCommand = new RelayCommand(_ => OpenSettings(), _ => !IsBusy);
        OpenBackupsCommand = new RelayCommand(_ => OpenBackups(null), _ => !IsBusy);
        OpenRowBackupsCommand = new RelayCommand(name => OpenBackups(name as string), _ => !IsBusy);
        OpenEnvSnippetCommand = new RelayCommand(_ => OpenEnvSnippet(), _ => HasSelectedDatabase && HasEnvSnippet && !IsBusy);
        OpenRowEnvCommand = new RelayCommand(name => OpenEnvForName(name as string), _ => !IsBusy);
        CopyEnvSnippetCommand = new RelayCommand(_ => CopyEnvSnippet(), _ => HasEnvSnippet && !IsBusy);
        ClearFiltersCommand = new RelayCommand(_ => ClearFilters(), _ => HasActiveFilters && !IsBusy);
        DismissLastMessageCommand = new RelayCommand(_ => SetLastMessage(null));

        SelectedEngineFilter = EngineFilters[0];
        Reload();
    }

    public string CredentialsSummary
    {
        get
        {
            var settings = _settingsStore.Load();
            var engine = settings.Databases.ActiveSqlEngine ?? SqlEngine.Mysql;
            var (creds, label) = engine switch
            {
                SqlEngine.Mariadb => (settings.Databases.Mariadb, "MariaDB"),
                SqlEngine.Postgresql => (settings.Databases.Postgresql, "PostgreSQL"),
                SqlEngine.Mongodb => (settings.Databases.Mongodb, "MongoDB"),
                _ => (settings.Databases.Mysql, "MySQL")
            };
            return $"{label} credentials from settings: {creds.Username} / (use gear icon to change)";
        }
    }

    public ObservableCollection<DatabaseRecord> Databases { get; }
    public ObservableCollection<DatabaseEngineFilterOption> EngineFilters { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand OpenCreateDialogCommand { get; }
    public RelayCommand BackupCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand OpenBackupsCommand { get; }
    public RelayCommand OpenRowBackupsCommand { get; }
    public RelayCommand OpenEnvSnippetCommand { get; }
    public RelayCommand OpenRowEnvCommand { get; }
    public RelayCommand CopyEnvSnippetCommand { get; }
    public RelayCommand ClearFiltersCommand { get; }
    public RelayCommand DismissLastMessageCommand { get; }

    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
            {
                ApplyFilters();
                RaisePropertyChanged(nameof(HasActiveFilters));
                ClearFiltersCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public DatabaseEngineFilterOption SelectedEngineFilter
    {
        get => _selectedEngineFilter;
        set
        {
            if (SetProperty(ref _selectedEngineFilter, value))
            {
                ApplyFilters();
                RaisePropertyChanged(nameof(HasActiveFilters));
                ClearFiltersCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public string? LastMessage
    {
        get => _lastMessage;
        private set => SetLastMessage(value);
    }

    public bool HasLastMessage => !string.IsNullOrWhiteSpace(LastMessage);

    private void SetLastMessage(string? value)
    {
        if (SetProperty(ref _lastMessage, value))
        {
            RaisePropertyChanged(nameof(HasLastMessage));
        }
    }

    public DatabaseRecord? SelectedDatabase
    {
        get => _selectedDatabase;
        set
        {
            if (SetProperty(ref _selectedDatabase, value))
            {
                UpdateEnvSnippet();
                RaisePropertyChanged(nameof(HasSelectedDatabase));
                OpenEnvSnippetCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string EnvSnippet
    {
        get => _envSnippet;
        private set
        {
            if (SetProperty(ref _envSnippet, value))
            {
                RaisePropertyChanged(nameof(HasEnvSnippet));
                CopyEnvSnippetCommand?.RaiseCanExecuteChanged();
                OpenEnvSnippetCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSelectedDatabase => SelectedDatabase is not null;
    public bool HasEnvSnippet => !string.IsNullOrWhiteSpace(EnvSnippet);
    public bool HasActiveFilters =>
        !string.IsNullOrWhiteSpace(FilterText) ||
        SelectedEngineFilter.Engine is not null;
    public bool HasDatabases => Databases.Count > 0;
    public bool ShowEmptyState => !HasDatabases;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                OpenCreateDialogCommand.RaiseCanExecuteChanged();
                BackupCommand.RaiseCanExecuteChanged();
                DeleteCommand.RaiseCanExecuteChanged();
                OpenSettingsCommand.RaiseCanExecuteChanged();
                OpenBackupsCommand.RaiseCanExecuteChanged();
                OpenRowBackupsCommand.RaiseCanExecuteChanged();
                OpenEnvSnippetCommand.RaiseCanExecuteChanged();
                OpenRowEnvCommand.RaiseCanExecuteChanged();
                CopyEnvSnippetCommand.RaiseCanExecuteChanged();
                ClearFiltersCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string BusyMessage
    {
        get => _busyMessage;
        private set => SetProperty(ref _busyMessage, value);
    }

    private readonly List<DatabaseRecord> _allDatabases = [];

    public void Reload()
    {
        var previousName = SelectedDatabase?.Name;
        _allDatabases.Clear();
        _allDatabases.AddRange(_databaseManager.List());
        ApplyFilters();

        SelectedDatabase = previousName is null
            ? Databases.FirstOrDefault()
            : Databases.FirstOrDefault(database =>
                string.Equals(database.Name, previousName, StringComparison.OrdinalIgnoreCase))
            ?? Databases.FirstOrDefault();
    }

    private void ApplyFilters()
    {
        var query = FilterText.Trim();
        var engine = SelectedEngineFilter.Engine;

        Databases.Clear();
        foreach (var entry in _allDatabases)
        {
            if (engine is not null && entry.Engine != engine.Value)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(query) &&
                entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase) != true)
            {
                continue;
            }

            Databases.Add(entry);
        }

        RaisePropertyChanged(nameof(HasDatabases));
        RaisePropertyChanged(nameof(ShowEmptyState));
    }

    private void ClearFilters()
    {
        FilterText = string.Empty;
        SelectedEngineFilter = EngineFilters[0];
    }

    private void OpenCreateDialog()
    {
        var siteOptions = new List<SiteLinkOptionViewModel>
        {
            new() { SiteId = null, Label = "No site link" }
        };

        foreach (var site in _siteManager.List().OrderBy(s => s.Domain, StringComparer.OrdinalIgnoreCase))
        {
            siteOptions.Add(new SiteLinkOptionViewModel
            {
                SiteId = site.Id,
                Label = site.Domain
            });
        }

        var dialogVm = new CreateDatabaseDialogViewModel(
            _databaseManager.ListEngines().ToList(),
            siteOptions,
            _databaseManager,
            _activity);
        var owner = Application.Current?.MainWindow;
        var dialog = new CreateDatabaseDialog
        {
            DataContext = dialogVm,
            Owner = owner
        };

        dialogVm.RequestClose += (_, _) => dialog.Close();
        dialogVm.DatabaseCreated += (_, record) =>
        {
            LastMessage = $"Created '{record.Name}' ({record.Engine}).";
            dialog.Close();
            Reload();
            SelectedDatabase = Databases.FirstOrDefault(database =>
                string.Equals(database.Name, record.Name, StringComparison.OrdinalIgnoreCase))
                ?? _allDatabases.FirstOrDefault(database =>
                    string.Equals(database.Name, record.Name, StringComparison.OrdinalIgnoreCase));
        };

        dialog.ShowDialog();
    }

    private void UpdateEnvSnippet()
    {
        EnvSnippet = SelectedDatabase is null
            ? string.Empty
            : _databaseManager.BuildEnvSnippet(SelectedDatabase.Name, SelectedDatabase.Engine);
    }

    private async Task BackupAsync(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        var result = await _activity.RunBackgroundAsync<string>(
            "Databases",
            $"Backup database '{name}'",
            () => Task.FromResult(_databaseManager.Backup(name)),
            setBusy: value => IsBusy = value,
            successMessage: SessionActivityMessages.DatabaseBackupCreated(name, name),
            setStatus: message => BusyMessage = message,
            onError: ex => HandleError("Backup Error", ex),
            busyMessage: $"Backing up '{name}'…",
            failureMessage: $"Backup failed for '{name}'.",
            formatSuccess: path => SessionActivityMessages.DatabaseBackupCreated(name, Path.GetFileName(path))).ConfigureAwait(true);

        if (!result.Succeeded || result.Value is null)
        {
            return;
        }

        LastMessage = $"Backup created: {result.Value}";
        Reload();
    }

    private async Task DeleteAsync(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        if (!ConfirmDialog.Show(
                Application.Current?.MainWindow,
                "Delete database?",
                $"Delete database '{name}'? This cannot be undone.",
                "Delete",
                isDanger: true))
        {
            return;
        }

        // Ask about associated backups
        var backups = _databaseManager.ListBackups(name);
        if (backups.Count > 0)
        {
            var msg = backups.Count == 1
                ? $"This database has 1 backup file. Delete it as well?"
                : $"This database has {backups.Count} backup files. Delete them as well?";

            var choice = System.Windows.MessageBox.Show(
                msg,
                "Delete database",
                System.Windows.MessageBoxButton.YesNoCancel,
                System.Windows.MessageBoxImage.Question);

            if (choice == System.Windows.MessageBoxResult.Cancel) return;

            if (choice == System.Windows.MessageBoxResult.Yes)
            {
                foreach (var backup in backups)
                {
                    try { File.Delete(backup.FullPath); } catch { }
                }
            }
        }

        var result = await _activity.RunBackgroundAsync<bool>(
            "Databases",
            $"Delete database '{name}'",
            () => Task.FromResult(_databaseManager.Delete(name)),
            setBusy: value => IsBusy = value,
            successMessage: SessionActivityMessages.DatabaseDeleted(name),
            setStatus: message => BusyMessage = message,
            onError: ex => HandleError("Delete Error", ex),
            busyMessage: $"Deleting '{name}'…",
            failureMessage: $"Delete failed for '{name}'.").ConfigureAwait(true);

        if (!result.Succeeded)
        {
            return;
        }

        if (result.Value == false)
        {
            LastMessage = $"Database '{name}' was not found.";
            return;
        }

        LastMessage = $"Deleted '{name}'.";
        Reload();
    }

    private void HandleError(string title, Exception ex)
    {
        if (ex is MongoToolMissingException mongoEx)
        {
            var choice = MessageDialog.Show(
                Application.Current?.MainWindow,
                title,
                $"{mongoEx.Message}\n\nInstall {mongoEx.ToolLabel} now?",
                StackrootDialogKind.Warning,
                StackrootDialogButtons.YesNo,
                okText: "Install");

            if (choice == StackrootDialogResult.Yes)
            {
                _ = InstallAndContinueAsync(mongoEx);
            }
        }
        else
        {
            MessageDialog.Show(Application.Current?.MainWindow, title, ex.Message, StackrootDialogKind.Error);
        }
    }

    private async Task InstallAndContinueAsync(MongoToolMissingException mongoEx)
    {
        IsBusy = true;
        BusyMessage = $"Installing {mongoEx.ToolLabel}…";
        try
        {
            var entry = _catalogStore.Load().Packages
                .FirstOrDefault(p => p.Type == mongoEx.NeededTool);
            if (entry is null)
            {
                MessageDialog.Show(Application.Current?.MainWindow, "Install Error",
                    $"No catalog entry found for {mongoEx.ToolLabel}.", StackrootDialogKind.Error);
                return;
            }

            if (_packages.IsInstalling(entry.Id))
            {
                BusyMessage = $"{mongoEx.ToolLabel} is already downloading…";
                return;
            }

            await _packages.InstallAsync(entry);
            MessageDialog.Show(Application.Current?.MainWindow, "Installed",
                $"{mongoEx.ToolLabel} installed. You can now try the operation again.",
                StackrootDialogKind.Info);
        }
        catch (Exception ex)
        {
            MessageDialog.Show(Application.Current?.MainWindow, "Install Error", ex.Message, StackrootDialogKind.Error);
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
        }
    }

    private void OpenEnvSnippet()
    {
        OpenEnvForName(SelectedDatabase?.Name);
    }

    private void OpenEnvForName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;

        var db = _allDatabases.FirstOrDefault(d =>
            string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
        if (db is null) return;

        var snippet = _databaseManager.BuildEnvSnippet(db.Name, db.Engine);
        if (string.IsNullOrWhiteSpace(snippet)) return;

        var dialogVm = new DatabaseEnvSnippetDialogViewModel(db.Name, snippet);
        var owner = Application.Current?.MainWindow;
        var dialog = new DatabaseEnvSnippetDialog
        {
            DataContext = dialogVm,
            Owner = owner
        };

        dialogVm.RequestClose += (_, _) => dialog.Close();
        dialog.ShowDialog();
    }

    private void CopyEnvSnippet()
    {
        if (string.IsNullOrWhiteSpace(EnvSnippet))
        {
            return;
        }

        Clipboard.SetText(EnvSnippet);
        LastMessage = "Copied .env snippet to clipboard.";
    }

    private void OpenSettings()
    {
        var dialogVm = new DatabasesSettingsDialogViewModel(
            _settingsStore,
            _serviceManager,
            _phpMyAdminManager,
            _appDomainConfigWriter,
            _diagnostics);
        var owner = Application.Current?.MainWindow;
        var dialog = new DatabasesSettingsDialog
        {
            DataContext = dialogVm,
            Owner = owner
        };

        dialogVm.RequestClose += (_, _) => dialog.Close();

        SettingsSaveFeedback.DeferredSettingsSave? deferred = null;
        dialogVm.SettingsSaved += (_, _) =>
        {
            deferred = new SettingsSaveFeedback.DeferredSettingsSave(
                "Saving database credentials…",
                "Database credentials saved and applied.",
                async () =>
                {
                    await dialogVm.ApplySavedChangesAsync();
                    RaisePropertyChanged(nameof(CredentialsSummary));
                });
        };

        dialog.ShowDialog();

        if (deferred is { } save)
        {
            _ = SettingsSaveFeedback.RunDeferredOnSessionActivityAsync(_activity, save);
        }
    }

    private void OpenBackups(string? databaseName)
    {
        var dialogVm = new DatabaseBackupsDialogViewModel(
            _databaseManager,
            _activity,
            databaseName);
        var owner = Application.Current?.MainWindow;
        var dialog = new DatabaseBackupsDialog
        {
            DataContext = dialogVm,
            Owner = owner
        };

        dialogVm.RequestClose += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => Reload();
        dialog.ShowDialog();
    }
}

public sealed class DatabaseEngineFilterOption(SqlEngine? engine, string label)
{
    public SqlEngine? Engine { get; } = engine;
    public string Label { get; } = label;

    public override string ToString() => Label;
}
