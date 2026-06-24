using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

using System.Windows.Input;
using System.Windows.Threading;

using Microsoft.Extensions.DependencyInjection;

using Stackroot.App.Commands;

using Stackroot.App.Helpers;

using Stackroot.App.Services;

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

using Stackroot.Core.Windows;

using PackageType = Stackroot.Core.Abstractions.PackageType;



namespace Stackroot.App.ViewModels;



public sealed class SitesViewModel : ViewModelBase

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

    private readonly IDiagnosticsReporter _diagnostics;

    private readonly SessionActivityReporter _activity;

    private bool _isRebuildingNginx;
    private int _reloadVersion;

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

        IDiagnosticsReporter diagnostics,

        SessionActivityReporter activity)

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

        _diagnostics = diagnostics;
        _activity = activity;



        PhpVersions = new ObservableCollection<PhpVersionOptionViewModel>();
        NodeVersions = new ObservableCollection<NodeVersionOptionViewModel>();

        Groups = new ObservableCollection<SiteGroupViewModel>();



        OpenAddSiteCommand = new RelayCommand(_ => OpenAddSiteDialog());

        OpenSettingsCommand = new RelayCommand(_ => OpenSitesSettingsDialog());

        OpenHostsCommand = new RelayCommand(_ => OpenHostsFile());

        RefreshCommand = new RelayCommand(_ => Reload());

        RebuildNginxCommand = new RelayCommand(_ => _ = RebuildNginxAsync(), _ => !IsRebuildingNginx);

        DismissErrorCommand = new RelayCommand(_ => ErrorMessage = string.Empty);

        DismissWarningCommand = new RelayCommand(_ => WarningMessage = string.Empty);



        LoadPhpVersions();
        LoadNodeVersions();

        Reload();

    }



    public ObservableCollection<PhpVersionOptionViewModel> PhpVersions { get; }
    public ObservableCollection<NodeVersionOptionViewModel> NodeVersions { get; }

    public ObservableCollection<SiteGroupViewModel> Groups { get; }



    public ICommand OpenAddSiteCommand { get; }

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



    private List<SiteRowViewModel> BuildRows(IReadOnlyList<Site> sites)

    {

        return sites

            .OrderBy(site => site.Domain, StringComparer.OrdinalIgnoreCase)

            .Select(site => new SiteRowViewModel(

                site,

                _siteManager,

                Reload,

                OpenManage,

                OpenEditDialog,

                DeleteSite,

                UpdateSitePhpVersion,

                OpenCreateDatabaseDialog,

                site => OnSiteRuntimeChanged(site)))

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

        var title = "Remove domain?";

        var message = $"Remove {site.Domain} from Stackroot? The nginx config and hosts entry will be removed.";

        var confirmText = "Remove";



        var result = ConfirmDialog.ShowWithCheckbox(
            Application.Current?.MainWindow, title, message, confirmText,
            isDanger: true, checkboxLabel: "Also delete site files");

        if (!result.Confirmed)

        {

            return;

        }



        try

        {

            ErrorMessage = string.Empty;

            RemoveSiteProcesses(site.Id);

            _siteManager.Delete(site.Id, forceDeleteFiles: result.IsChecked);

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

