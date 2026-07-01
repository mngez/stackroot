using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

using System.Windows.Input;
using System.Windows.Threading;

using Microsoft.Extensions.DependencyInjection;

using Stackroot.App.Commands;

using Stackroot.App.Helpers;

using Stackroot.App.Localization;

using Stackroot.App.Scheduling;

using Stackroot.App.Services;

using StackrootPaths = Stackroot.Core.Abstractions.StackrootPaths;

using Stackroot.App.Views;

using IDiagnosticsReporter = Stackroot.Core.Abstractions.IDiagnosticsReporter;

using Stackroot.Core.Catalog;

using Stackroot.Core.Databases;

using Stackroot.Core.Node;

using SqlEngine = Stackroot.Core.Abstractions.SqlEngine;

using Stackroot.Core.Settings;

using Stackroot.Core.Sites;

using Stackroot.Core.Sites.Management;

using Stackroot.Core.Sites.Models;

using Stackroot.Core.Supervisor;

using Stackroot.Core.IO;

using Stackroot.Core.Windows;

using PackageType = Stackroot.Core.Abstractions.PackageType;



namespace Stackroot.App.ViewModels;



public sealed class SitesViewModel : ViewModelBase, IDisposable

{

    private readonly SiteManager _siteManager;

    private readonly SettingsStore _settingsStore;

    private readonly HostsFileEditor _hostsFileEditor;

    private readonly PackageCatalogStore _catalogStore;

    private readonly InstallRegistryStore _registryStore;

    private readonly DatabaseManager _databaseManager;

    private readonly IReadOnlyList<SiteTemplateDefinition> _templates;

    private readonly IServiceProvider _services;

    private readonly NginxWebStackRebuilder _nginxWebStackRebuilder;

    private readonly GlobalProcessManager _processManager;

    private readonly TaskSchedulerService _scheduler;

    private readonly SiteBackupService _backupService;

    private readonly SiteRestoreService _restoreService;

    private readonly Services.SiteBackupTracker _backupTracker;

    private readonly StackrootPaths _paths;

    private readonly IDiagnosticsReporter _diagnostics;

    private readonly SessionActivityReporter _activity;

    private readonly BackgroundAlertService _alertService;

    private bool _isRebuildingNginx;
    private int _reloadVersion;
    private EventHandler<CriticalOperationEventArgs>? _operationStartedHandler;
    private EventHandler<CriticalOperationEventArgs>? _operationEndedHandler;

    private string _wwwPath = string.Empty;

    private string _errorMessage = string.Empty;

    private string _warningMessage = string.Empty;



    public SitesViewModel(

        SiteManager siteManager,

        SettingsStore settingsStore,

        HostsFileEditor hostsFileEditor,

        PackageCatalogStore catalogStore,

        InstallRegistryStore registryStore,

        DatabaseManager databaseManager,

        IReadOnlyList<SiteTemplateDefinition> templates,

        IServiceProvider services,

        NginxWebStackRebuilder nginxWebStackRebuilder,

        GlobalProcessManager processManager,

        TaskSchedulerService scheduler,

        SiteBackupService backupService,

        SiteRestoreService restoreService,

        Services.SiteBackupTracker backupTracker,

        StackrootPaths paths,

        IDiagnosticsReporter diagnostics,

        SessionActivityReporter activity,

        BackgroundAlertService alertService)

    {

        _siteManager = siteManager;

        _settingsStore = settingsStore;

        _hostsFileEditor = hostsFileEditor;

        _catalogStore = catalogStore;

        _registryStore = registryStore;

        _databaseManager = databaseManager;

        _templates = templates;

        _services = services;

        _nginxWebStackRebuilder = nginxWebStackRebuilder;

        _processManager = processManager;

        _scheduler = scheduler;

        _backupService = backupService;

        _restoreService = restoreService;

        _backupTracker = backupTracker;

        _paths = paths;

        _diagnostics = diagnostics;
        _activity = activity;
        _alertService = alertService;



        PhpVersions = new ObservableCollection<PhpVersionOptionViewModel>();
        NodeVersions = new ObservableCollection<NodeVersionOptionViewModel>();

        Groups = new ObservableCollection<SiteGroupViewModel>();



        OpenAddSiteCommand = new RelayCommand(_ => OpenAddSiteDialog());

        ImportSiteCommand = new RelayCommand(_ => ImportSite());

        OpenSettingsCommand = new RelayCommand(_ => OpenSitesSettingsDialog());

        OpenHostsCommand = new RelayCommand(_ => OpenHostsFile());

        RefreshCommand = new RelayCommand(_ => Reload());

        RebuildNginxCommand = new RelayCommand(_ => _ = RebuildNginxAsync(), _ => !IsRebuildingNginx);

        DismissErrorCommand = new RelayCommand(_ => ErrorMessage = string.Empty);

        DismissWarningCommand = new RelayCommand(_ => WarningMessage = string.Empty);

        _operationStartedHandler = (_, args) =>
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
                FindRow(args.SiteId)?.SetBackingUp(true);
            else
                dispatcher.BeginInvoke(() => FindRow(args.SiteId)?.SetBackingUp(true));
        };

        _operationEndedHandler = (_, args) =>
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
                FindRow(args.SiteId)?.SetBackingUp(false);
            else
                dispatcher.BeginInvoke(() => FindRow(args.SiteId)?.SetBackingUp(false));
        };

        _backupTracker.OperationStarted += _operationStartedHandler;
        _backupTracker.OperationEnded += _operationEndedHandler;



        LoadPhpVersions();
        LoadNodeVersions();

        Reload();

    }

    public void Dispose()
    {
        _backupTracker.OperationStarted -= _operationStartedHandler;
        _backupTracker.OperationEnded -= _operationEndedHandler;
    }


    public ObservableCollection<PhpVersionOptionViewModel> PhpVersions { get; }
    public ObservableCollection<NodeVersionOptionViewModel> NodeVersions { get; }

    public ObservableCollection<SiteGroupViewModel> Groups { get; }



    public ICommand OpenAddSiteCommand { get; }

    public ICommand ImportSiteCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public ICommand OpenHostsCommand { get; }

    public ICommand RefreshCommand { get; }

    public RelayCommand RebuildNginxCommand { get; }

    public ICommand DismissErrorCommand { get; }

    public ICommand DismissWarningCommand { get; }



    public string WwwPath

    {

        get => _wwwPath;

        private set => SetProperty(ref _wwwPath, value);

    }



    public string ErrorMessage

    {

        get => _errorMessage;

        set

        {

            if (SetProperty(ref _errorMessage, value))

            {

                RaisePropertyChanged(nameof(HasError));

            }

        }

    }



    public string WarningMessage

    {

        get => _warningMessage;

        set

        {

            if (SetProperty(ref _warningMessage, value))

            {

                RaisePropertyChanged(nameof(HasWarning));

            }

        }

    }



    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool HasWarning => !string.IsNullOrWhiteSpace(WarningMessage);

    public bool HasSites => Groups.Sum(group => group.Sites.Count) > 0;

    public bool ShowEmptyState => !HasSites;

    public bool IsRebuildingNginx
    {
        get => _isRebuildingNginx;
        private set
        {
            if (SetProperty(ref _isRebuildingNginx, value))
            {
                RebuildNginxCommand.RaiseCanExecuteChanged();
            }
        }
    }



    public void Reload()

    {

        _ = ReloadAsync();

        RefreshShellNav();

    }

    private void RefreshShellNav()
    {
        try
        {
            var shell = _services.GetRequiredService<ShellViewModel>();
            shell.RefreshSiteNavFromStore(_siteManager);
        }
        catch { /* non-critical */ }
    }

    private async Task ReloadAsync()
    {
        var version = Interlocked.Increment(ref _reloadVersion);
        try
        {
            var snapshot = await Task.Run(BuildReloadSnapshot).ConfigureAwait(false);
            if (version != Volatile.Read(ref _reloadVersion))
            {
                return;
            }

            await RunOnUiAsync(() => ApplyReloadSnapshot(snapshot)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() => ErrorMessage = ex.Message).ConfigureAwait(false);
        }
    }

    private SitesReloadSnapshot BuildReloadSnapshot()
    {
        var settings = _settingsStore.Load();
        var dashboard = _siteManager.GetDashboard();

        return new SitesReloadSnapshot(
            SitePaths.EffectiveWwwPath(settings.General.WwwPath),
            BuildRows(dashboard.Featured),
            BuildRows(dashboard.Active),
            BuildRows(dashboard.Disabled));
    }

    private void ApplyReloadSnapshot(SitesReloadSnapshot snapshot)
    {
        WwwPath = snapshot.WwwPath;
        Groups.Clear();

        if (snapshot.Featured.Count > 0)
        {
            Groups.Add(new SiteGroupViewModel("Featured", "Pinned to the top", snapshot.Featured));
        }

        if (snapshot.Active.Count > 0)
        {
            Groups.Add(new SiteGroupViewModel("Sites", null, snapshot.Active));
        }

        if (snapshot.Disabled.Count > 0)
        {
            Groups.Add(new SiteGroupViewModel("Disabled", "Not served by nginx", snapshot.Disabled));
        }

        RaisePropertyChanged(nameof(HasSites));
        RaisePropertyChanged(nameof(ShowEmptyState));
    }

    private static Task RunOnUiAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action, DispatcherPriority.Background).Task;
    }



    private async Task RebuildNginxAsync()
    {
        if (IsRebuildingNginx)
        {
            return;
        }

        IsRebuildingNginx = true;
        ErrorMessage = string.Empty;
        try
        {
            WarningMessage = await _nginxWebStackRebuilder.RebuildAsync();
            Reload();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsRebuildingNginx = false;
        }
    }



    public void UpdateSitePhpVersion(SiteRowViewModel row, string? phpVersionId)

    {

        try

        {

            ErrorMessage = string.Empty;

            var value = phpVersionId == "no-php" ? string.Empty : phpVersionId ?? string.Empty;

            row.UpdateSite(_siteManager.Update(row.Site.Id, new UpdateSiteInput { PhpVersionId = value }));

            _ = RebuildNginxAsync();

        }

        catch (Exception ex)

        {

            ErrorMessage = ex.Message;

            Reload();

        }

    }



    private SiteRowViewModel? FindRow(string siteId) =>
        Groups.SelectMany(g => g.Sites)
            .FirstOrDefault(r => string.Equals(r.Site.Id, siteId, StringComparison.OrdinalIgnoreCase));

    private List<SiteRowViewModel> BuildRows(IReadOnlyList<Site> sites)

    {

        return sites

            .OrderBy(site => site.Domain, StringComparer.OrdinalIgnoreCase)

            .Select(site =>
            {
                var row = new SiteRowViewModel(

                    site,

                    _siteManager,

                    Reload,

                    OpenManage,

                    OpenEditDialog,

                    DeleteSite,

                    UpdateSitePhpVersion,

                    OpenCreateDatabaseDialog,

                    BackupSite,

                    site => OnSiteRuntimeChanged(site));

                if (_backupTracker.IsActiveAny(site.Id))
                    row.SetBackingUp(true);

                return row;
            })

            .ToList();

    }



    private void OnSiteRuntimeChanged(Site site)
    {
        _activity.LogSuccess("Sites", SessionActivityMessages.SiteEnabled(site.Domain, site.Enabled));
        _ = RebuildNginxAsync();
    }

    private void OpenManage(Site site)

    {

        _services.GetRequiredService<ShellViewModel>().NavigateToSiteManage(site.Id);

    }



    private void OpenAddSiteDialog()

    {

        var settings = _settingsStore.Load();

        var dialogVm = new AddSiteDialogViewModel(_templates, PhpVersions, NodeVersions, settings.General.WwwPath);

        var owner = Application.Current?.MainWindow;

        var dialog = new AddSiteDialog

        {

            DataContext = dialogVm,

            Owner = owner

        };



        dialogVm.RequestClose += (_, _) => dialog.Close();

        dialogVm.SiteCreated += (_, input) =>

        {

            try

            {

                ErrorMessage = string.Empty;

                WarningMessage = string.Empty;

                var site = _siteManager.Create(input);

                _activity.LogSuccess("Sites", SessionActivityMessages.SiteCreated(input.Domain));

                _ = RebuildNginxAsync();

                if (_settingsStore.Load().Sites.AutoHosts &&

                    !string.IsNullOrWhiteSpace(_hostsFileEditor.LastError))

                {

                    WarningMessage = _hostsFileEditor.LastError;

                }



                dialog.Close();

                Reload();

                if (dialogVm.GoToSiteDashboard)
                {
                    _services.GetRequiredService<ShellViewModel>().NavigateToSiteManage(site.Id);
                }
            }

            catch (Exception ex)

            {

                dialogVm.ErrorMessage = ex.Message;

            }

        };



        dialog.ShowDialog();

    }



    private void OpenCreateDatabaseDialog(Site site)

    {

        var siteOptions = new List<SiteLinkOptionViewModel>

        {

            new() { SiteId = null, Label = "No site link" }

        };

        foreach (var linkedSite in _siteManager.List().OrderBy(s => s.Domain, StringComparer.OrdinalIgnoreCase))

        {

            siteOptions.Add(new SiteLinkOptionViewModel

            {

                SiteId = linkedSite.Id,

                Label = linkedSite.Domain

            });

        }



        var dialogVm = new CreateDatabaseDialogViewModel(

            [SqlEngine.Mysql, SqlEngine.Mariadb],

            siteOptions,

            _databaseManager,

            _activity,

            site.Id);

        var owner = Application.Current?.MainWindow;

        var dialog = new CreateDatabaseDialog

        {

            DataContext = dialogVm,

            Owner = owner

        };



        dialogVm.RequestClose += (_, _) => dialog.Close();

        dialogVm.DatabaseCreated += (_, record) =>

        {

            ErrorMessage = string.Empty;

            dialog.Close();

            WarningMessage = $"Database '{record.Name}' created. Open Databases to copy the .env snippet.";

        };



        dialog.ShowDialog();

    }



    private void OpenEditDialog(Site site)

    {

        var settings = _settingsStore.Load();

        var dialogVm = new EditSiteDialogViewModel(site, _templates, PhpVersions, settings.General.WwwPath, settings.NginxHttp);

        var owner = Application.Current?.MainWindow;

        var dialog = new EditSiteDialog

        {

            DataContext = dialogVm,

            Owner = owner

        };



        dialogVm.RequestClose += (_, _) => dialog.Close();

        dialogVm.SiteSaved += (_, patch) =>

        {

            try

            {

                ErrorMessage = string.Empty;

                _siteManager.Update(site.Id, patch);

                _activity.LogSuccess("Sites", SessionActivityMessages.SiteUpdated(site.Domain));

                dialog.Close();

                _ = RebuildNginxAsync();

            }

            catch (Exception ex)

            {

                dialogVm.ErrorMessage = ex.Message;

            }

        };



        dialog.ShowDialog();

    }



    private void DeleteSite(Site site)

    {

        if (site.Enabled)
        {
            ErrorMessage = $"Disable {site.Domain} before removing it.";
            return;
        }

        var hasDatabases = _databaseManager.List().Any(db => string.Equals(db.SiteId, site.Id, StringComparison.OrdinalIgnoreCase));
        var hasScheduledTasks = _scheduler.List().Any(t => string.Equals(t.SiteId, site.Id, StringComparison.OrdinalIgnoreCase));
        var hasProcesses = _processManager.List(site.Id).Count > 0;

        var title = "Remove domain?";
        var message = $"Remove {site.Domain} from Stackroot? The nginx config and hosts entry will be removed.";

        var result = DeleteSiteDialog.Show(
            Application.Current?.MainWindow,
            title,
            message,
            hasDatabases,
            hasScheduledTasks,
            hasProcesses);

        if (!result.Confirmed)

        {

            return;

        }



        try

        {

            ErrorMessage = string.Empty;

            if (result.DeleteProcesses)
            {
                RemoveSiteProcesses(site.Id);
            }

            if (result.DeleteScheduledTasks)
            {
                _scheduler.DeleteBySiteId(site.Id);
            }

            _siteManager.Delete(site.Id, forceDeleteFiles: result.DeleteFiles, deleteDatabases: result.DeleteDatabases);

            _activity.LogSuccess("Sites", SessionActivityMessages.SiteDeleted(site.Domain));

            _ = RebuildNginxAsync();

        }

        catch (Exception ex)

        {

            ErrorMessage = ex.Message;

        }

    }



    private void RemoveSiteProcesses(string siteId)

    {

        foreach (var process in _processManager.List(siteId))

        {

            _processManager.Remove(process.Id);

        }

    }

    private void BackupSite(Site site)
    {
        var dialogVm = new BackupSiteDialogViewModel(
            site,
            _databaseManager,
            _processManager,
            _scheduler);

        var owner = Application.Current?.MainWindow;
        var dialog = new Views.BackupSiteDialog { DataContext = dialogVm, Owner = owner };

        bool confirmed = false;
        dialogVm.RequestClose += (_, result) => { confirmed = result; dialog.Close(); };
        dialog.ShowDialog();

        if (!confirmed) return;

        var options = dialogVm.BuildOptions();

        _ = Task.Run(async () =>
        {
            var result = await SiteBackupHelper.RunBackupAsync(
                site, options,
                _backupService, _backupTracker, _activity,
                _siteManager, _processManager, _scheduler,
                _settingsStore, _paths);

            await RunOnUiAsync(() =>
            {
                if (result.Success)
                {
                    _alertService.Raise(new BackgroundAlert(
                        BackgroundAlertKind.Success,
                        LocalizationManager.Get("Loc.BackgroundAlert.BackupComplete", "Backup Complete"),
                        string.Format(LocalizationManager.Get("Loc.BackgroundAlert.BackupComplete.Body", "{0} backed up successfully."), site.Domain),
                        Detail: result.ResultPath,
                        Actions: [new BackgroundAlertAction(
                            LocalizationManager.Get("Loc.BackgroundAlert.RestoreComplete.OpenFolder", "Open Folder"),
                            () => OpenBackupFolder(result.ResultPath!))]));

                    if (result.SiteDeleted)
                    {
                        Reload();
                        _ = RebuildNginxAsync();
                    }
                }
                else
                {
                    _alertService.Raise(new BackgroundAlert(
                        BackgroundAlertKind.Error,
                        LocalizationManager.Get("Loc.BackgroundAlert.BackupFailed", "Backup Failed"),
                        string.Format(LocalizationManager.Get("Loc.BackgroundAlert.BackupFailed.Body", "Could not back up {0}."), site.Domain),
                        result.Exception));
                }
            });
        });

        BackupStartedDialog.Show(Application.Current?.MainWindow, site.Domain);
    }

    private static void OpenBackupFolder(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (dir is not null && Directory.Exists(dir))
                System.Diagnostics.Process.Start("explorer.exe", dir);
        }
        catch { /* best effort */ }
    }

    private void ImportSite()
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select site backup",
            Filter = "Site backups (*.zip)|*.zip",
            CheckFileExists = true
        };
        if (picker.ShowDialog() != true) return;

        BackupManifest manifest;
        try
        {
            manifest = _restoreService.ReadManifest(picker.FileName);
        }
        catch (Exception ex)
        {
            _activity.LogError("Sites", $"Failed to read backup: {ex.Message}", ex);
            return;
        }

        var conflict = _restoreService.CheckConflicts(manifest, picker.FileName);
        // Provide all domain names (primary + aliases) so the live domain-conflict check in the ViewModel is accurate
        var allExistingDomains = _siteManager.List()
            .SelectMany(s => new[] { s.Domain }.Concat(s.DomainAliases ?? []));
        var dialogVm = new ImportSiteDialogViewModel(manifest, conflict, allExistingDomains);
        var owner = Application.Current?.MainWindow;
        var dialog = new Views.ImportSiteDialog { DataContext = dialogVm, Owner = owner };
        bool confirmed = false;
        dialogVm.RequestClose += (_, result) => { confirmed = result; dialog.Close(); };
        dialog.ShowDialog();
        if (!confirmed) return;

        var options = dialogVm.BuildOptions();
        var newDomain = dialogVm.NewDomain;
        var zipPath = picker.FileName;
        var importDisplayDomain = !string.IsNullOrWhiteSpace(newDomain) ? newDomain : manifest.SiteDomain;
        var importTrackingId = Guid.NewGuid().ToString("N");
        _backupTracker.Begin(importTrackingId, importDisplayDomain, SiteOperationType.Import);
        var progressId = _activity.Begin("Import", "Importing site…");

        _ = Task.Run(async () =>
        {
            try
            {
                var progress = new Progress<string>(msg => _activity.UpdateProgress(progressId, "Import", msg));
                var newSite = await _restoreService.ImportSiteAsync(zipPath, newDomain, options, progress);
                await RunOnUiAsync(() =>
                {
                    _backupTracker.End(importTrackingId, SiteOperationType.Import);
                    _activity.Complete(progressId, "Import", $"Site imported as {newSite.Domain}");
                    Reload();
                    _ = RebuildNginxAsync();
                    var importedId = newSite.Id;
                    _alertService.Raise(new BackgroundAlert(
                        BackgroundAlertKind.Success,
                        LocalizationManager.Get("Loc.BackgroundAlert.ImportComplete", "Import Complete"),
                        string.Format(LocalizationManager.Get("Loc.BackgroundAlert.ImportComplete.Body", "{0} imported successfully."), newSite.Domain),
                        Actions: [new BackgroundAlertAction(
                            LocalizationManager.Get("Loc.BackgroundAlert.ImportComplete.ManageSite", "Manage Site"),
                            () => _services.GetRequiredService<ShellViewModel>().NavigateToSiteManage(importedId))]));
                });
            }
            catch (Exception ex)
            {
                await RunOnUiAsync(() =>
                {
                    _backupTracker.End(importTrackingId, SiteOperationType.Import);
                    _activity.Fail(progressId, "Import", $"Import failed: {ex.Message}", ex);
                    _alertService.Raise(new BackgroundAlert(
                        BackgroundAlertKind.Error,
                        LocalizationManager.Get("Loc.BackgroundAlert.ImportFailed", "Import Failed"),
                        LocalizationManager.Get("Loc.BackgroundAlert.ImportFailed.Body", "Could not import the site backup."),
                        ex));
                });
            }
        });
    }

    private void OpenSitesSettingsDialog()

    {

        var dialogVm = new SitesSettingsDialogViewModel(_settingsStore);

        var owner = Application.Current?.MainWindow;

        var dialog = new SitesSettingsDialog

        {

            DataContext = dialogVm,

            Owner = owner

        };



        dialogVm.RequestClose += (_, _) => dialog.Close();

        SettingsSaveFeedback.DeferredSettingsSave? deferred = null;
        dialogVm.SettingsSaved += (_, _) =>
        {
            deferred = new SettingsSaveFeedback.DeferredSettingsSave(
                "Saving site settings…",
                dialogVm.StatusMessage);
        };

        dialog.ShowDialog();

        if (deferred is { } save)
        {
            _ = SettingsSaveFeedback.RunDeferredOnSessionActivityAsync(_activity, save);
        }

    }

    private void LoadPhpVersions()
    {
        PhpVersions.Clear();

        PhpVersions.Add(new PhpVersionOptionViewModel("no-php", "no-php"));



        var installedIds = _registryStore.List()

            .Where(package => package.Type == PackageType.Php)

            .Select(package => package.Id)

            .ToHashSet(StringComparer.OrdinalIgnoreCase);



        foreach (var entry in _catalogStore.List(PackageType.Php)

                     .Where(entry => installedIds.Contains(entry.Id))

                     .OrderByDescending(entry => entry.Id, StringComparer.OrdinalIgnoreCase))

        {

            PhpVersions.Add(new PhpVersionOptionViewModel(entry.Id, entry.Label));

        }

    }

    private void LoadNodeVersions()
    {
        NodeVersions.Clear();
        NodeVersions.Add(new NodeVersionOptionViewModel("none", "None"));

        // Load async to avoid UI deadlock
        _ = Task.Run(async () =>
        {
            try
            {
                var nodeManager = _services.GetRequiredService<NodeManager>();
                var versions = await nodeManager.ListInstalledVersionsAsync();
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    foreach (var v in versions)
                        NodeVersions.Add(new NodeVersionOptionViewModel(v, $"Node.js {v}"));
                });
            }
            catch { /* Node not installed yet */ }
        });
    }

    private void OpenHostsFile()
    {
        var hostsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "drivers", "etc", "hosts");

        if (!File.Exists(hostsPath))
        {
            MessageBox.Show("Hosts file not found at:\n" + hostsPath,
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "notepad.exe",
                Arguments = hostsPath,
                UseShellExecute = true,
                Verb = "runas"
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not open hosts file:\n" + ex.Message,
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

}



public sealed record TemplateOptionViewModel(string Id, string Label);

internal sealed record SitesReloadSnapshot(
    string WwwPath,
    List<SiteRowViewModel> Featured,
    List<SiteRowViewModel> Active,
    List<SiteRowViewModel> Disabled);

