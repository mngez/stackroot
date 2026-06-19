using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Stackroot.App.Helpers;
using Stackroot.App.Commands;
using Stackroot.App.Services;
using Stackroot.App.Views;
using Stackroot.Core.Abstractions;
using Stackroot.Core.AdminTools;
using Stackroot.Core.Catalog;
using Stackroot.Core.Databases;
using Stackroot.Core.Nginx;
using Stackroot.Core.Services;
using Stackroot.Core.Settings;
using Stackroot.Core.Sites.Management;
using Stackroot.Core.Windows;

namespace Stackroot.App.ViewModels;

public sealed class ServicesViewModel : ViewModelBase
{
    private const string VersionsHint =
        "Choose one version, then click Install. The dialog closes and progress appears in the tray (↓).";

    private readonly ServiceManager _serviceManager;
    private readonly SettingsStore _settingsStore;
    private readonly InstallRegistryStore _registryStore;
    private readonly PackageCatalogStore _catalogStore;
    private readonly PackageInstallCoordinator _packages;
    private readonly SiteManager _siteManager;
    private readonly PhpMyAdminManager _phpMyAdminManager;
    private readonly PhpRedisAdminManager _phpRedisAdminManager;
    private readonly MailpitManager _mailpitManager;
    private readonly AppDomainConfigWriter _appDomainConfigWriter;
    private readonly NginxWebStackRebuilder _nginxWebStackRebuilder;
    private readonly StackrootPaths _paths;
    private readonly IProcessJobManager _jobManager;
    private readonly InstallProgressTracker _installTracker;
    private readonly SessionActivityReporter _activity;
    private readonly SessionActivityCoordinator _activityCoordinator;
    private bool _isRefreshing;
    private bool _isRebuilding;
    private bool _initialized;
    private int _refreshVersion;
    private int _liveServiceUpdateScheduled;
    private readonly ConcurrentDictionary<string, ServiceInfo> _pendingLiveServiceUpdates = new(StringComparer.OrdinalIgnoreCase);
    private string? _stackNotice;

    public ServicesViewModel(
        ServiceManager serviceManager,
        SettingsStore settingsStore,
        InstallRegistryStore registryStore,
        PackageCatalogStore catalogStore,
        PackageInstallCoordinator packages,
        SiteManager siteManager,
        PhpMyAdminManager phpMyAdminManager,
        PhpRedisAdminManager phpRedisAdminManager,
        MailpitManager mailpitManager,
        AppDomainConfigWriter appDomainConfigWriter,
        NginxWebStackRebuilder nginxWebStackRebuilder,
        StackrootPaths paths,
        IProcessJobManager jobManager,
        InstallProgressTracker installTracker,
        SessionActivityReporter activity,
        SessionActivityCoordinator activityCoordinator)
    {
        _serviceManager = serviceManager;
        _settingsStore = settingsStore;
        _registryStore = registryStore;
        _catalogStore = catalogStore;
        _packages = packages;
        _siteManager = siteManager;
        _phpMyAdminManager = phpMyAdminManager;
        _phpRedisAdminManager = phpRedisAdminManager;
        _mailpitManager = mailpitManager;
        _appDomainConfigWriter = appDomainConfigWriter;
        _nginxWebStackRebuilder = nginxWebStackRebuilder;
        _paths = paths;
        _jobManager = jobManager;
        _installTracker = installTracker;
        _activity = activity;
        _activityCoordinator = activityCoordinator;

        _mailpitManager.StatusChanged += (_, _) => _ = RefreshMailpitStatusAsync();
        _serviceManager.LiveStatusChanged += OnServiceLiveStatusChanged;

        Groups = [];
        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(force: true), _ => !IsRefreshing && !IsRebuilding);
        RebuildNginxCommand = new RelayCommand(_ => _ = RebuildNginxAsync(), _ => !IsRefreshing && !IsRebuilding);
        DismissStackNoticeCommand = new RelayCommand(_ => SetStackNotice(null));

        BuildGroups();
        _ = InitializeInBackgroundAsync();
    }

    public void BeginLoading()
    {
        // State is warmed at app startup; live events keep statuses current.
    }

    public void RefreshAfterDeferredStartup()
    {
        _ = RefreshAsync(force: true);
    }

    public Task RefreshFromExternalAsync() => RefreshAsync(force: true);

    private async Task InitializeInBackgroundAsync()
    {
        if (_initialized || IsRefreshing)
        {
            return;
        }

        try
        {
            await RefreshAsync(force: true).ConfigureAwait(false);
        }
        catch
        {
            // Background warmup should not surface startup failures in the shell.
        }
    }

    public ObservableCollection<ServiceGroupViewModel> Groups { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand RebuildNginxCommand { get; }
    public RelayCommand DismissStackNoticeCommand { get; }

    public string? StackNotice
    {
        get => _stackNotice;
        private set => SetStackNotice(value);
    }

    public bool HasStackNotice => !string.IsNullOrWhiteSpace(StackNotice);

    private void SetStackNotice(string? value)
    {
        if (SetProperty(ref _stackNotice, value))
        {
            RaisePropertyChanged(nameof(HasStackNotice));
        }
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                RebuildNginxCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsRebuilding
    {
        get => _isRebuilding;
        private set
        {
            if (SetProperty(ref _isRebuilding, value))
            {
                RefreshCommand.RaiseCanExecuteChanged();
                RebuildNginxCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private void BuildGroups()
    {
        Groups.Clear();
        var settings = _settingsStore.Load();

        foreach (var category in Enum.GetValues<ServiceCategory>())
        {
            var group = new ServiceGroupViewModel { Title = category.ToString() };
            var definitions = SettingsDefaults.ServiceDefinitions.Where(definition => definition.Category == category);

            foreach (var definition in definitions)
            {
                if (!settings.Services.TryGetValue(definition.Id, out var serviceSettings))
                {
                    serviceSettings = SettingsDefaults.DefaultServices()[definition.Id];
                }

                var packageType = ServicePackageMapper.ToPackageType(definition.Id);
                var hasInstalledVersion = _registryStore.List(packageType).Count > 0;
                var activePackageId = serviceSettings.PackageId ?? definition.PackageId;
                var installed = !string.IsNullOrWhiteSpace(activePackageId) && _registryStore.IsInstalled(activePackageId);
                var enabled = definition.Id == ServiceId.Mailpit
                    ? settings.Mailpit.Enabled
                    : serviceSettings.Enabled;

                ServiceEntryViewModel? row = null;
                row = new ServiceEntryViewModel
                {
                    Definition = definition,
                    Settings = serviceSettings,
                    StartCommand = new RelayCommand(
                        _ => _ = StartAsync(definition.Id),
                        _ => CanStart(row)),
                    StopCommand = new RelayCommand(
                        _ => _ = StopAsync(definition.Id),
                        _ => CanStop(row)),
                    RestartCommand = new RelayCommand(
                        _ => _ = RestartAsync(definition.Id),
                        _ => CanRestart(row)),
                    InstallCommand = new RelayCommand(
                        _ => OpenVersionsDialog(definition.Id),
                        _ => CanOpenVersions(row)),
                    VersionsCommand = new RelayCommand(
                        _ => OpenVersionsDialog(definition.Id),
                        _ => CanOpenVersions(row)),
                    SettingsCommand = new RelayCommand(
                        _ => OpenServiceSettingsDialog(definition.Id),
                        _ => CanOpenSettings(row)),
                    Enabled = enabled,
                    HasInstalledVersion = hasInstalledVersion,
                    Installed = installed,
                    ActivePackageLabel = string.IsNullOrWhiteSpace(activePackageId)
                        ? "—"
                        : _catalogStore.GetById(activePackageId)?.Label ?? activePackageId
                };
                row.OnPackageSelected = packageId => _ = SetActivePackageByIdAsync(definition.Id, packageId);
                row.SetSelectedPackageIdWithoutCallback(activePackageId);

                group.Items.Add(row);
            }

            if (group.Items.Count > 0)
            {
                Groups.Add(group);
            }
        }
    }

    private static bool CanStart(ServiceEntryViewModel? item)
    {
        return item is not null
            && item.Installed
            && item.Enabled
            && !item.IsBusy
            && !item.IsInstalling
            && !item.IsRunning;
    }

    private static bool CanStop(ServiceEntryViewModel? item)
    {
        return item is not null
            && item.Installed
            && item.Enabled
            && !item.IsBusy
            && !item.IsInstalling
            && item.IsRunning;
    }

    private static bool CanRestart(ServiceEntryViewModel? item)
    {
        return item is not null
            && item.Installed
            && item.Enabled
            && !item.IsBusy
            && !item.IsInstalling;
    }

    private static bool CanOpenVersions(ServiceEntryViewModel? item)
    {
        return item is not null && !item.IsBusy && !item.IsInstalling;
    }

    private static bool CanOpenSettings(ServiceEntryViewModel? item)
    {
        return item is not null && !item.IsBusy && !item.IsInstalling;
    }

    private void OpenVersionsDialog(ServiceId id)
    {
        var item = FindItem(id);
        if (item is null)
        {
            return;
        }

        var packageType = ServicePackageMapper.ToPackageType(id);
        var catalog = _catalogStore.List(packageType);
        if (catalog.Count == 0)
        {
            StackrootDialogs.ShowWarning(
                Application.Current?.MainWindow,
                "Package catalog empty",
                $"No package versions were found for {item.Name}.",
                $"Catalog file:{Environment.NewLine}{_catalogStore.Path}{Environment.NewLine}{Environment.NewLine}Restart the app after updating Stackroot. If this persists, delete catalog.json and restart.");
            return;
        }

        var activePackageId = item.Settings.PackageId ?? item.Definition.PackageId;
        var dialogVm = new PackageVersionsDialogViewModel(
            $"{item.Name} versions",
            VersionsHint,
            catalog,
            _registryStore,
            () =>
            {
                if (id == ServiceId.Mailpit)
                {
                    return _settingsStore.Load().Mailpit.PackageId;
                }

                return _settingsStore.Load().Services[id].PackageId ?? item.Definition.PackageId;
            },
            package => InstallPackageAsync(id, package),
            package => UninstallPackageAsync(id, package),
            package => SetActivePackageAsync(id, package));

        var owner = Application.Current?.MainWindow;
        var dialog = new PackageVersionsDialog
        {
            DataContext = dialogVm,
            Owner = owner
        };

        dialogVm.RequestClose += (_, _) => dialog.Close();
        dialog.ShowDialog();
    }

    private void OpenServiceSettingsDialog(ServiceId id)
    {
        var item = FindItem(id);
        if (item is null)
        {
            return;
        }

        if (id == ServiceId.Mailpit)
        {
            OpenMailpitSettings(item);
            return;
        }

        var settings = _settingsStore.Load().Services[id];
        var dialogVm = new ServiceSettingsDialogViewModel(
            _settingsStore,
            item.Definition,
            settings,
            ResolvePackageLabel(settings.PackageId ?? item.Definition.PackageId));

        var owner = Application.Current?.MainWindow;
        var dialog = new ServiceSettingsDialog
        {
            DataContext = dialogVm,
            Owner = owner
        };

        dialogVm.RequestClose += (_, _) => dialog.Close();

        SettingsSaveFeedback.DeferredSettingsSave? deferred = null;
        var restartAfterSave = false;
        dialogVm.SettingsSaved += (_, _) =>
        {
            item.Settings = _settingsStore.Load().Services[id];
            item.Enabled = item.Settings.Enabled;
            item.ActivePackageLabel = ResolvePackageLabel(item.Settings.PackageId ?? item.Definition.PackageId);
            item.RefreshCommandStates();

            if (dialogVm.ClosedAfterSave)
            {
                restartAfterSave = dialogVm.NeedsRestart && item.IsRunning;
                deferred = new SettingsSaveFeedback.DeferredSettingsSave(
                    SessionActivityMessages.SavingSettings(item.Name),
                    dialogVm.StatusMessage);
                return;
            }

            if (!string.IsNullOrWhiteSpace(dialogVm.StatusMessage))
            {
                _activity.LogSuccess("Services", dialogVm.StatusMessage);
            }
        };

        dialog.ShowDialog();

        if (deferred is { } save)
        {
            var shouldRestart = restartAfterSave;
            _ = SettingsSaveFeedback.RunDeferredOnSessionActivityAsync(_activity, save, async () =>
            {
                await RefreshAsync(force: true);
                item.Message = save.SuccessMessage;
                if (shouldRestart
                    && item.Enabled
                    && item.Installed
                    && item.Definition.Runtime != ServiceRuntime.Library)
                {
                    await RestartAsync(id);
                }
            });
        }
    }

    private string ResolvePackageLabel(string? packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return "—";
        }

        var entry = _catalogStore.GetById(packageId);
        return entry?.Label ?? packageId;
    }

    private async Task RefreshAsync(bool force = false)
    {
        if (IsRefreshing)
        {
            return;
        }

        if (_initialized && !force)
        {
            return;
        }

        IsRefreshing = true;
        var version = Interlocked.Increment(ref _refreshVersion);
        try
        {
            var definitions = await RunOnUiAsync(() => Groups
                .SelectMany(group => group.Items)
                .Select(item => item.Definition)
                .ToList()).ConfigureAwait(false);
            var snapshot = await BuildRefreshSnapshotAsync(definitions).ConfigureAwait(false);
            if (version != Volatile.Read(ref _refreshVersion))
            {
                return;
            }

            await RunOnUiAsync(() =>
            {
                ApplyRefreshSnapshot(snapshot);
                _initialized = true;
            }).ConfigureAwait(false);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private async Task<IReadOnlyList<ServiceRefreshSnapshot>> BuildRefreshSnapshotAsync(
        IReadOnlyList<ServiceDefinition> definitions)
    {
        return await Task.Run(() =>
        {
            var settings = _settingsStore.Load();
            var live = _serviceManager.ListLiveAsync().GetAwaiter().GetResult();
            var mailpitStatus = _mailpitManager.GetStatusAsync().GetAwaiter().GetResult();
            var snapshots = new List<ServiceRefreshSnapshot>(definitions.Count);
            foreach (var definition in definitions)
            {
                settings.Services.TryGetValue(definition.Id, out var serviceSettings);
                serviceSettings ??= SettingsDefaults.DefaultServices()[definition.Id];

                var packageType = ServicePackageMapper.ToPackageType(definition.Id);
                var installed = _registryStore.List(packageType)
                    .OrderByDescending(pkg => pkg.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(pkg => new ServicePackageOptionViewModel
                    {
                        Id = pkg.Id,
                        Label = _catalogStore.GetById(pkg.Id)?.Label ?? pkg.Id
                    })
                    .ToList();

                var activePackageId = serviceSettings.PackageId ?? definition.PackageId;
                var liveInfo = live.FirstOrDefault(row =>
                    string.Equals(row.Id, definition.Id.ToString().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase));
                var isRunning = liveInfo?.PortOpen == true;
                string? detailsOverride = null;
                if (definition.Id == ServiceId.Mailpit)
                {
                    detailsOverride = mailpitStatus.Installed
                        ? $"SMTP: {mailpitStatus.SmtpEndpoint} · UI: {mailpitStatus.WebUrl}"
                        : null;
                    isRunning = mailpitStatus.Installed && mailpitStatus.Running;
                }

                snapshots.Add(new ServiceRefreshSnapshot(
                    definition.Id,
                    serviceSettings,
                    definition.Id == ServiceId.Mailpit ? settings.Mailpit.Enabled : serviceSettings.Enabled,
                    installed.Count > 0,
                    !string.IsNullOrWhiteSpace(activePackageId) && _registryStore.IsInstalled(activePackageId),
                    string.IsNullOrWhiteSpace(activePackageId)
                        ? "—"
                        : _catalogStore.GetById(activePackageId)?.Label ?? activePackageId,
                    isRunning,
                    liveInfo?.Message,
                    detailsOverride,
                    activePackageId,
                    installed));
            }

            return snapshots;
        }).ConfigureAwait(false);
    }

    private void ApplyRefreshSnapshot(IReadOnlyList<ServiceRefreshSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            var item = FindItem(snapshot.Id);
            if (item is null)
            {
                continue;
            }

            item.Settings = snapshot.Settings;
            item.HasInstalledVersion = snapshot.HasInstalledVersion;
            item.Enabled = snapshot.Enabled;
            item.Installed = snapshot.Installed;
            item.ActivePackageLabel = snapshot.ActivePackageLabel;
            if (!item.IsInstalling)
            {
                item.Message = null;
                item.DetailsOverride = snapshot.DetailsOverride;
                ServiceStatusPresenter.Apply(item, snapshot.IsRunning, snapshot.LiveMessage);
            }

            item.InstalledVersions.Clear();
            foreach (var package in snapshot.InstalledVersions)
            {
                item.InstalledVersions.Add(package);
            }

            item.SetSelectedPackageIdWithoutCallback(snapshot.ActivePackageId);
            item.NotifyVersionComboChanged();
            item.RefreshCommandStates();
            item.NotifyRunningStateChanged();
        }
    }

    private void OnServiceLiveStatusChanged(object? sender, ServiceInfo info)
    {
        _pendingLiveServiceUpdates[info.Id] = info;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _liveServiceUpdateScheduled, 1) == 1)
        {
            return;
        }

        dispatcher.BeginInvoke(ApplyPendingLiveServiceUpdates, DispatcherPriority.Background);
    }

    private void ApplyPendingLiveServiceUpdates()
    {
        Interlocked.Exchange(ref _liveServiceUpdateScheduled, 0);
        var updates = _pendingLiveServiceUpdates.ToArray();
        _pendingLiveServiceUpdates.Clear();
        foreach (var (_, info) in updates)
        {
            ApplyLiveServiceInfo(info);
        }
    }

    private void ApplyLiveServiceInfo(ServiceInfo info)
    {
        if (string.Equals(info.Id, "mailpit", StringComparison.OrdinalIgnoreCase))
        {
            _ = RefreshMailpitStatusAsync();
            return;
        }

        if (!Enum.TryParse<ServiceId>(info.Id, true, out var serviceId))
        {
            return;
        }

        var item = FindItem(serviceId);
        if (item is null)
        {
            return;
        }

        ApplyLiveServiceInfoToItem(item, info);
    }

    private static void ApplyLiveServiceInfoToItem(ServiceEntryViewModel item, ServiceInfo liveInfo)
    {
        if (item.IsInstalling)
        {
            return;
        }

        if (liveInfo.Enabled is bool enabled)
        {
            item.Enabled = enabled;
        }

        if (liveInfo.Installed is bool installed)
        {
            item.Installed = installed;
        }

        if (item.IsBusy
            && string.Equals(item.StatusText, "Starting", StringComparison.Ordinal)
            && liveInfo.PortOpen != true)
        {
            return;
        }

        if (item.IsBusy
            && string.Equals(item.StatusText, "Stopping", StringComparison.Ordinal)
            && liveInfo is not { PortOpen: false })
        {
            return;
        }

        if (item.IsBusy
            && string.Equals(item.StatusText, "Restarting", StringComparison.Ordinal)
            && liveInfo.PortOpen != true)
        {
            return;
        }

        if (liveInfo.Status == ServiceStatus.Error)
        {
            item.IsBusy = false;
            item.Message = liveInfo.Message;
            item.StatusText = "Error";
            item.StatusColor = "#EAAAB0";
        }
        else if (liveInfo.Status == ServiceStatus.Starting)
        {
            item.IsBusy = false;
            item.Message = liveInfo.Message;
            item.StatusText = "Starting";
            item.StatusColor = "#E9BD5B";
        }
        else
        {
            item.IsBusy = false;
            item.Message = liveInfo.Message;
            ServiceStatusPresenter.Apply(item, liveInfo.PortOpen == true, liveInfo.Message);
        }

        item.RefreshCommandStates();
        item.NotifyRunningStateChanged();
    }

    private async Task RefreshMailpitStatusAsync()
    {
        var item = FindItem(ServiceId.Mailpit);
        if (item is null)
        {
            return;
        }

        var settings = _settingsStore.Load();
        var mailpitStatus = await _mailpitManager.GetStatusAsync().ConfigureAwait(false);
        await RunOnUiAsync(() =>
        {
            item.Settings = settings.Services[ServiceId.Mailpit];
            item.Enabled = settings.Mailpit.Enabled;
            item.DetailsOverride = mailpitStatus.Installed
                ? $"SMTP: {mailpitStatus.SmtpEndpoint} · UI: {mailpitStatus.WebUrl}"
                : null;

            if (!item.IsInstalling)
            {
                ServiceStatusPresenter.Apply(
                    item,
                    mailpitStatus.Installed && mailpitStatus.Running,
                    null);
            }

            item.RefreshCommandStates();
            item.NotifyRunningStateChanged();
        }).ConfigureAwait(false);
    }

    private void PopulateInstalledVersions(ServiceEntryViewModel item)
    {
        var packageType = ServicePackageMapper.ToPackageType(item.Definition.Id);
        var installed = _registryStore.List(packageType)
            .OrderByDescending(pkg => pkg.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        item.InstalledVersions.Clear();
        foreach (var pkg in installed)
        {
            var catalogEntry = _catalogStore.GetById(pkg.Id);
            item.InstalledVersions.Add(new ServicePackageOptionViewModel
            {
                Id = pkg.Id,
                Label = catalogEntry?.Label ?? pkg.Id
            });
        }

        var activePackageId = item.Settings.PackageId ?? item.Definition.PackageId;
        item.SetSelectedPackageIdWithoutCallback(activePackageId);
        item.NotifyVersionComboChanged();
    }

    private async Task RebuildNginxAsync()
    {
        if (IsRebuilding)
        {
            return;
        }

        IsRebuilding = true;
        SetStackNotice(null);
        var activityId = _activity.Begin("Services", "Rebuilding nginx…");
        try
        {
            var message = await _nginxWebStackRebuilder.RebuildAsync();
            SetStackNotice(message);
            _activity.Complete(activityId, "Services", SessionActivityMessages.NginxRebuilt(message));
            await RefreshAsync(force: true);
        }
        catch (Exception ex)
        {
            SetStackNotice(ex.Message);
            _activity.Fail(activityId, "Services", ex.Message, ex);
        }
        finally
        {
            IsRebuilding = false;
        }
    }

    private async Task InstallPackageAsync(ServiceId serviceId, PackageEntry package)
    {
        var item = FindItem(serviceId);
        if (!await TryPreparePhpDependencyAsync(serviceId, package, item))
        {
            return;
        }

        await RunOnUiAsync(() =>
        {
            if (item is not null)
            {
                item.IsInstalling = true;
                item.InstallPercent = 0;
                item.Message = $"Installing {package.Label}...";
                item.RefreshCommandStates();
            }
        });

        try
        {
            if (_registryStore.IsInstalled(package.Id))
            {
                return;
            }

            if (_packages.IsInstalling(package.Id))
            {
                await RunOnUiAsync(() =>
                {
                    if (item is not null)
                    {
                        item.Message = $"{package.Label} is already downloading.";
                    }
                });
                return;
            }

            await _packages.InstallAsync(
                package,
                progress => RunOnUi(() =>
                {
                    if (item is not null)
                    {
                        item.InstallPercent = progress.Percent;
                        item.InstallPhase = progress.Phase.ToString();
                        item.Message = $"{progress.Message} ({progress.Percent}%)";
                    }
                }));

            var current = _settingsStore.Load().Services[serviceId];
            _settingsStore.UpdateService(serviceId, current with
            {
                PackageId = package.Id,
                Enabled = true
            });
            if (serviceId is ServiceId.Mysql or ServiceId.Mariadb)
            {
                await RunOnUiAsync(() =>
                {
                    if (item is not null)
                    {
                        item.Message = $"Installed {package.Label}. Initializing database and applying credentials...";
                    }
                });

                var settings = _settingsStore.Load();
                var provisioned = await MariaDbProvisioner.ProvisionAfterInstallAsync(
                    _paths,
                    _registryStore,
                    settings,
                    serviceId,
                    async (id, token) => await _serviceManager.StartAsync(id.ToString().ToLowerInvariant(), token),
                    CancellationToken.None);

                var credentialsApplied = provisioned
                    || await _serviceManager.SyncEnabledSqlCredentialsAsync();
                await _phpMyAdminManager.ApplyAsync();
                _appDomainConfigWriter.Write();

                await RunOnUiAsync(() =>
                {
                    if (item is null)
                    {
                        return;
                    }

                    item.Settings = _settingsStore.Load().Services[serviceId];
                    item.Enabled = item.Settings.Enabled;
                    item.ActivePackageLabel = package.Label;
                    item.Message = credentialsApplied
                        ? $"Installed {package.Label}. Database password applied from settings."
                        : $"Installed {package.Label}. Database is running but password setup is still pending.";
                });
            }
            else
            {
                if (serviceId == ServiceId.Mailpit)
                {
                    await _mailpitManager.ApplyAsync();
                    _appDomainConfigWriter.Write();
                }

                await RunOnUiAsync(() =>
                {
                    if (item is null)
                    {
                        return;
                    }

                    item.Settings = _settingsStore.Load().Services[serviceId];
                    item.Enabled = serviceId == ServiceId.Mailpit
                        ? _settingsStore.Load().Mailpit.Enabled
                        : item.Settings.Enabled;
                    item.ActivePackageLabel = package.Label;
                    item.Message = $"Installed {package.Label}. Open settings (⚙) to enable auto-start.";
                });
            }
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() =>
            {
                if (item is not null)
                {
                    item.Message = ex.Message;
                    item.StatusText = "Error";
                    item.StatusColor = "#EAAAB0";
                }
            });
        }
        finally
        {
            await RunOnUiAsync(() =>
            {
                if (item is not null)
                {
                    item.IsInstalling = false;
                    item.InstallPercent = 0;
                    item.InstallPhase = null;
                    item.RefreshCommandStates();
                }
            });

            await RefreshAsync(force: true);
        }
    }

    private async Task SetActivePackageByIdAsync(ServiceId serviceId, string? packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return;
        }

        var current = _settingsStore.Load().Services[serviceId];
        var currentId = current.PackageId ?? FindItem(serviceId)?.Definition.PackageId;
        if (string.Equals(packageId, currentId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var package = _catalogStore.GetById(packageId);
        if (package is null)
        {
            return;
        }

        await SetActivePackageAsync(serviceId, package);
    }

    private async Task SetActivePackageAsync(ServiceId serviceId, PackageEntry package)
    {
        var item = FindItem(serviceId);
        var current = _settingsStore.Load().Services[serviceId];
        _settingsStore.UpdateService(serviceId, current with { PackageId = package.Id });
        if (item is not null)
        {
            item.Settings = _settingsStore.Load().Services[serviceId];
            item.ActivePackageLabel = package.Label;
            item.Message = $"{package.Label} is now the active version.";
        }

        await RefreshAsync(force: true);
    }

    private async Task UninstallPackageAsync(ServiceId serviceId, PackageEntry package)
    {
        var confirmed = ConfirmDialog.Show(
            Application.Current?.MainWindow,
            $"Uninstall {FindItem(serviceId)?.Name ?? serviceId.ToString()}",
            $"Remove {package.Label} and delete its files from disk?",
            "Uninstall",
            isDanger: true);
        if (!confirmed)
        {
            return;
        }

        var item = FindItem(serviceId);
        await RunOnUiAsync(() =>
        {
            if (item is not null)
            {
                item.IsInstalling = true;
                item.Message = $"Removing {package.Label}...";
                item.RefreshCommandStates();
            }
        });

        try
        {
            var current = _settingsStore.Load().Services[serviceId];
            var wasActive = string.Equals(current.PackageId, package.Id, StringComparison.OrdinalIgnoreCase);
            var shouldStop = wasActive && item is not null && item.Enabled;

            await Task.Run(async () =>
            {
                if (shouldStop)
                {
                    await StopBeforeUninstallAsync(serviceId).ConfigureAwait(false);
                }

                await _packages.UninstallAsync(package).ConfigureAwait(false);
            }).ConfigureAwait(true);

            if (wasActive)
            {
                var replacement = _registryStore
                    .List(ServicePackageMapper.ToPackageType(serviceId))
                    .FirstOrDefault(installed => !string.Equals(installed.Id, package.Id, StringComparison.OrdinalIgnoreCase));

                var fallbackPackageId = replacement?.Id ?? item?.Definition.PackageId;
                _settingsStore.UpdateService(serviceId, current with { PackageId = fallbackPackageId });
            }

            if (item is not null)
            {
                item.Settings = _settingsStore.Load().Services[serviceId];
                item.Message = $"Removed {package.Label}.";
                _activity.LogSuccess("Services", SessionActivityMessages.PackageUninstalled(package.Label));
            }
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() =>
            {
                if (item is not null)
                {
                    item.Message = ex.Message;
                    item.StatusText = "Error";
                    item.StatusColor = "#EAAAB0";
                }
            });
        }
        finally
        {
            await RunOnUiAsync(() =>
            {
                if (item is not null)
                {
                    item.IsInstalling = false;
                    item.RefreshCommandStates();
                }
            });

            await RefreshAsync(force: true);
        }
    }

    private async Task StartAsync(ServiceId id)
    {
        var item = FindItem(id);
        if (item is null || item.IsBusy || !item.Installed || !item.Enabled)
        {
            return;
        }

        item.IsBusy = true;
        item.Message = null;
        item.StatusText = "Starting";
        item.StatusColor = "#E9BD5B";
        ServiceInfo? result = null;
        try
        {
            result = id switch
            {
                ServiceId.Nginx => await _serviceManager.StartNginxAsync(),
                ServiceId.Redis => await _serviceManager.StartRedisAsync(),
                ServiceId.Memcached or ServiceId.Postgresql or ServiceId.Mongodb or ServiceId.Mysql or ServiceId.Mariadb =>
                    await _serviceManager.StartAsync(id.ToString().ToLowerInvariant()),
                _ => new ServiceInfo { Message = "Start is not wired for this service yet." }
            };

            if (!string.IsNullOrWhiteSpace(result.Message) && result.Status != ServiceStatus.Running)
            {
                item.Message = result.Message;
                item.StatusText = "Error";
                item.StatusColor = "#EAAAB0";
            }
        }
        catch (Exception ex)
        {
            item.Message = ex.Message;
            item.StatusText = "Error";
            item.StatusColor = "#EAAAB0";
        }
        finally
        {
            item.IsBusy = false;
            item.RefreshCommandStates();
            await RefreshAsync(force: true);
        }
    }

    private async Task StopBeforeUninstallAsync(ServiceId id)
    {
        var item = FindItem(id);
        if (item is null || !item.Installed || !item.Enabled)
        {
            return;
        }

        try
        {
            if (id == ServiceId.Nginx)
            {
                await _serviceManager.StopAsync("nginx", item.Port).ConfigureAwait(false);
            }
            else if (id is ServiceId.Redis or ServiceId.Memcached or ServiceId.Postgresql or ServiceId.Mongodb or ServiceId.Mysql or ServiceId.Mariadb)
            {
                await _serviceManager.StopAsync(id.ToString().ToLowerInvariant(), item.Port).ConfigureAwait(false);
            }
            else if (id == ServiceId.Mailpit)
            {
                await _mailpitManager.StopAsync().ConfigureAwait(false);
            }

            if (id is ServiceId.Mysql or ServiceId.Mariadb or ServiceId.Postgresql or ServiceId.Mongodb)
            {
                var settings = _settingsStore.Load().Services[id];
                for (var attempt = 0; attempt < 40; attempt++)
                {
                    if (!await _serviceManager.IsPortOpenAsync(settings.Host, settings.Port).ConfigureAwait(false))
                    {
                        break;
                    }

                    await Task.Delay(250).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() =>
            {
                if (item is not null)
                {
                    item.Message = ex.Message;
                }
            });
        }
    }

    private async Task StopAsync(ServiceId id)
    {
        var item = FindItem(id);
        if (item is null || item.IsBusy || !item.Installed || !item.Enabled)
        {
            return;
        }

        item.IsBusy = true;
        item.Message = null;
        item.StatusText = "Stopping";
        item.StatusColor = "#E9BD5B";
        try
        {
            switch (id)
            {
                case ServiceId.Nginx:
                    await _serviceManager.StopAsync("nginx");
                    break;
                case ServiceId.Redis:
                case ServiceId.Memcached:
                case ServiceId.Postgresql:
                case ServiceId.Mongodb:
                case ServiceId.Mysql:
                case ServiceId.Mariadb:
                    await _serviceManager.StopAsync(id.ToString().ToLowerInvariant());
                    break;
                default:
                    item.Message = "Stop is not wired for this service yet.";
                    break;
            }
        }
        catch (Exception ex)
        {
            item.Message = ex.Message;
            item.StatusText = "Error";
            item.StatusColor = "#EAAAB0";
        }
        finally
        {
            item.IsBusy = false;
            item.RefreshCommandStates();
            await RefreshAsync(force: true);
        }
    }

    private async Task RestartAsync(ServiceId id)
    {
        var item = FindItem(id);
        if (item is null || item.IsBusy || !item.Installed || !item.Enabled)
        {
            return;
        }

        var serviceKey = id.ToString().ToLowerInvariant();
        var activityId = _activity.Begin("Services", $"Restarting {item.Name}…");
        item.IsBusy = true;
        item.StatusText = "Restarting";
        item.StatusColor = "#E9BD5B";
        using (_activityCoordinator.Suppress(serviceKey))
        {
            try
            {
                var result = await Task.Run(async () =>
                        await _serviceManager.RestartAsync(serviceKey).ConfigureAwait(false))
                    .ConfigureAwait(true);

                if (result.PortOpen == true || result.Status == ServiceStatus.Running)
                {
                    item.StatusText = id is ServiceId.Imagemagick or ServiceId.Gdlibs ? "Ready" : "Running";
                    item.StatusColor = "#8FD6B6";
                    item.Message = null;
                    _activity.Complete(activityId, "Services", SessionActivityMessages.ServiceAction(item.Name, "restarted", true));
                }
                else if (!string.IsNullOrWhiteSpace(result.Message))
                {
                    item.Message = result.Message;
                    item.StatusText = "Error";
                    item.StatusColor = "#EAAAB0";
                    _activity.Fail(activityId, "Services", SessionActivityMessages.ServiceAction(item.Name, "restart", false, result.Message));
                }
                else
                {
                    item.StatusText = "Stopped";
                    item.StatusColor = "#91A0B5";
                    _activity.Fail(activityId, "Services", SessionActivityMessages.ServiceAction(item.Name, "restart", false));
                }
            }
            catch (Exception ex)
            {
                item.Message = ex.Message;
                item.StatusText = "Error";
                item.StatusColor = "#EAAAB0";
                _activity.Fail(activityId, "Services", ex.Message, ex);
            }
            finally
            {
                item.IsBusy = false;
                item.RefreshCommandStates();
                await RefreshAsync(force: true);
            }
        }
    }

    private ServiceEntryViewModel? FindItem(ServiceId id)
    {
        return Groups.SelectMany(group => group.Items).FirstOrDefault(item => item.Definition.Id == id);
    }

    private bool HasInstalledVersion(ServiceId id)
    {
        var packageType = ServicePackageMapper.ToPackageType(id);
        return _registryStore.List(packageType).Count > 0;
    }

    private bool IsActivePackageInstalled(ServiceEntryViewModel item)
    {
        var packageId = item.Settings.PackageId ?? item.Definition.PackageId;
        return !string.IsNullOrWhiteSpace(packageId) && _registryStore.IsInstalled(packageId);
    }

    private async Task<bool> TryPreparePhpDependencyAsync(
        ServiceId serviceId,
        PackageEntry package,
        ServiceEntryViewModel? item)
    {
        if (package.RequiresPhp is null)
        {
            return true;
        }

        string? selectedPhpId = null;
        var confirmed = false;
        await RunOnUiAsync(() =>
        {
            confirmed = PackageInstallWithPhpDialogHost.TryPrompt(
                Application.Current?.MainWindow,
                package,
                _catalogStore,
                _registryStore,
                requirementFallback: null,
                out selectedPhpId);
        });

        if (!confirmed || string.IsNullOrWhiteSpace(selectedPhpId))
        {
            return false;
        }

        try
        {
            await PackageInstallWithPhpDialogHost.EnsurePhpInstalledAsync(
                selectedPhpId,
                _catalogStore,
                _registryStore,
                _packages,
                progress => RunOnUi(() =>
                {
                    if (item is not null)
                    {
                        item.IsInstalling = true;
                        item.InstallPercent = progress.Percent;
                        item.InstallPhase = progress.Phase.ToString();
                        item.Message = $"{progress.Message} ({progress.Percent}%)";
                    }
                }));

            var settings = _settingsStore.Load();
            _settingsStore.UpdatePhp(settings.Php with { ActiveVersionId = selectedPhpId });
            _settingsStore.UpdateService(
                serviceId,
                settings.Services[serviceId] with { PhpVersionId = selectedPhpId });
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() =>
            {
                if (item is not null)
                {
                    item.Message = ex.Message;
                }
            });
            return false;
        }

        return true;
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }

    private static Task RunOnUiAsync(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    private static Task<T> RunOnUiAsync<T>(Func<T> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return Task.FromResult(action());
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    private void OpenMailpitSettings(ServiceEntryViewModel item)
    {
        var dialogVm = new MailpitSettingsDialogViewModel(_settingsStore);
        var owner = Application.Current?.MainWindow;
        var dialog = new MailpitSettingsDialog
        {
            DataContext = dialogVm,
            Owner = owner
        };

        dialogVm.RequestClose += (_, _) => dialog.Close();

        SettingsSaveFeedback.DeferredSettingsSave? deferred = null;
        dialogVm.SettingsSaved += (_, _) =>
        {
            item.Settings = _settingsStore.Load().Services[ServiceId.Mailpit];
            item.Enabled = _settingsStore.Load().Mailpit.Enabled;
            deferred = new SettingsSaveFeedback.DeferredSettingsSave(
                "Saving Mailpit settings…",
                dialogVm.StatusMessage,
                async () =>
                {
                    await _mailpitManager.ApplyAsync();
                    await RefreshAsync(force: true);
                });
        };

        dialog.ShowDialog();

        if (deferred is { } save)
        {
            _ = SettingsSaveFeedback.RunDeferredOnSessionActivityAsync(_activity, save);
        }
    }
}

internal sealed record ServiceRefreshSnapshot(
    ServiceId Id,
    ServicePortSettings Settings,
    bool Enabled,
    bool HasInstalledVersion,
    bool Installed,
    string ActivePackageLabel,
    bool IsRunning,
    string? LiveMessage,
    string? DetailsOverride,
    string? ActivePackageId,
    List<ServicePackageOptionViewModel> InstalledVersions);
