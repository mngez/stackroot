using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;

using Stackroot.App.Commands;
using Stackroot.App.Helpers;
using Stackroot.App.Views;

using Stackroot.Core.Abstractions;

using Stackroot.Core.Catalog;

using Stackroot.Core.Services;

using Stackroot.Core.Services.Php;

using Stackroot.Core.Settings;

using Stackroot.App.Services;

using Stackroot.Core.Windows;



namespace Stackroot.App.ViewModels;



public sealed class PhpViewModel : ViewModelBase

{

    private readonly SettingsStore _settingsStore;

    private readonly InstallRegistryStore _registryStore;

    private readonly PackageCatalogStore _catalogStore;

    private readonly PackageInstallCoordinator _packages;

    private readonly ServiceManager _serviceManager;

    private readonly PhpConfigWriter _phpConfigWriter;

    private readonly PhpExtensionManager _extensionManager;

    private readonly PeclInstaller _peclInstaller;

    private readonly StackrootBinManager _binManager;

    private readonly InstallProgressTracker _installTracker;

    private readonly NginxWebStackRebuilder _nginxWebStackRebuilder;

    private readonly SessionActivityReporter _activity;

    private readonly ICollectionView _phpInstallQueueView;

    private bool _isRefreshing;

    private bool _refreshAgain;

    private readonly HashSet<string> _addingVersionIds = new(StringComparer.OrdinalIgnoreCase);

    private string _selectedCatalogPackageId = string.Empty;

    private string _statusMessage = string.Empty;

    private bool _showPhpInstallQueue;



    public PhpViewModel(

        SettingsStore settingsStore,

        InstallRegistryStore registryStore,

        PackageCatalogStore catalogStore,

        PackageInstallCoordinator packages,

        ServiceManager serviceManager,

        PhpConfigWriter phpConfigWriter,

        PhpExtensionManager extensionManager,

        PeclInstaller peclInstaller,

        StackrootBinManager binManager,

        InstallProgressTracker installTracker,

        NginxWebStackRebuilder nginxWebStackRebuilder,

        SessionActivityReporter activity)

    {

        _settingsStore = settingsStore;

        _registryStore = registryStore;

        _catalogStore = catalogStore;

        _packages = packages;

        _serviceManager = serviceManager;

        _phpConfigWriter = phpConfigWriter;

        _extensionManager = extensionManager;

        _peclInstaller = peclInstaller;

        _binManager = binManager;

        _installTracker = installTracker;

        _nginxWebStackRebuilder = nginxWebStackRebuilder;

        _activity = activity;

        _installTracker.Changed += (_, _) => OnInstallTrackerChanged();



        _phpInstallQueueView = CollectionViewSource.GetDefaultView(_installTracker.Items);

        _phpInstallQueueView.Filter = obj =>

            obj is InstallQueueItemViewModel item && ShouldShowInPhpInstallQueue(item);



        InstalledVersions = [];

        CatalogPhpPackages = [];

        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(), _ => !_isRefreshing);

        InstallCommand = new RelayCommand(_ => InstallSelected(), _ => CanInstallSelected());

        EnsureFastCgiCommand = new RelayCommand(_ => _ = EnsureFastCgiAsync(), _ => !_isRefreshing);

        SetActiveCommand = new RelayCommand(arg => _ = SetActiveVersionAsync(arg as string), _ => !_isRefreshing);

        OpenRuntimeSettingsCommand = new RelayCommand(_ => OpenRuntimeSettingsDialog(), _ => !_isRefreshing);

        OpenVersionSettingsCommand = new RelayCommand(arg => OpenVersionSettingsDialog(arg as string), _ => !_isRefreshing);

        OpenExtensionsCommand = new RelayCommand(arg => OpenExtensionsDialog(arg as string), _ => !_isRefreshing);

        UninstallCommand = new RelayCommand(

            arg => _ = UninstallVersionAsync(arg as string),

            arg => !_isRefreshing

                && arg is string id

                && !IsPackageInstalling(id)

                && !IsVersionRemoving(id));

        DismissStatusMessageCommand = new RelayCommand(_ => StatusMessage = string.Empty);

        LoadCatalogPackages();

        _ = RefreshAsync();

    }



    public ObservableCollection<PhpVersionRowViewModel> InstalledVersions { get; }

    public ObservableCollection<PackageEntry> CatalogPhpPackages { get; }

    public ICollectionView PhpInstallQueue => _phpInstallQueueView;

    public RelayCommand RefreshCommand { get; }

    public RelayCommand InstallCommand { get; }

    public RelayCommand EnsureFastCgiCommand { get; }

    public RelayCommand SetActiveCommand { get; }

    public RelayCommand OpenRuntimeSettingsCommand { get; }

    public RelayCommand OpenVersionSettingsCommand { get; }

    public RelayCommand OpenExtensionsCommand { get; }

    public RelayCommand UninstallCommand { get; }

    public RelayCommand DismissStatusMessageCommand { get; }



    public bool IsBusy => _isRefreshing;



    public bool ShowPhpInstallQueue => _showPhpInstallQueue;

    public int ActivePhpInstallCount => _installTracker.Items.Count(item =>

        item.IsActive && item.PackageId.StartsWith("php-", StringComparison.OrdinalIgnoreCase));

    private bool ShouldShowInPhpInstallQueue(InstallQueueItemViewModel item)
    {
        if (!item.PackageId.StartsWith("php-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (item.IsActive)
        {
            return true;
        }

        return item.Phase == InstallPhase.Done && !IsInstalledVersionListed(item.PackageId);
    }

    private void UpdatePhpInstallQueuePresentation()
    {
        _phpInstallQueueView.Refresh();

        var show = false;
        foreach (InstallQueueItemViewModel item in _installTracker.Items)
        {
            if (!ShouldShowInPhpInstallQueue(item))
            {
                continue;
            }

            show = true;
            break;
        }

        if (SetProperty(ref _showPhpInstallQueue, show))
        {
        }

        RaisePropertyChanged(nameof(ActivePhpInstallCount));
    }

    private bool IsInstalledVersionListed(string packageId) =>
        InstalledVersions.Any(row => string.Equals(row.Id, packageId, StringComparison.OrdinalIgnoreCase));

    private bool IsVersionRemoving(string versionId) =>
        InstalledVersions.FirstOrDefault(row => string.Equals(row.Id, versionId, StringComparison.OrdinalIgnoreCase))

            is { IsRemoving: true };



    private bool CanInstallSelected() =>

        !string.IsNullOrWhiteSpace(SelectedCatalogPackageId)

        && !IsPackageInstalling(SelectedCatalogPackageId)

        && !_registryStore.IsInstalled(SelectedCatalogPackageId);



    private bool IsPackageInstalling(string packageId) =>

        _packages.IsInstalling(packageId)

        || _installTracker.Items.Any(item =>

            item.IsActive && string.Equals(item.PackageId, packageId, StringComparison.OrdinalIgnoreCase));



    private void RaiseCommandStates()

    {

        RefreshCommand.RaiseCanExecuteChanged();

        InstallCommand.RaiseCanExecuteChanged();

        EnsureFastCgiCommand.RaiseCanExecuteChanged();

        SetActiveCommand.RaiseCanExecuteChanged();

        OpenRuntimeSettingsCommand.RaiseCanExecuteChanged();

        OpenVersionSettingsCommand.RaiseCanExecuteChanged();

        OpenExtensionsCommand.RaiseCanExecuteChanged();

        UninstallCommand.RaiseCanExecuteChanged();

        RaisePropertyChanged(nameof(IsBusy));

        UpdatePhpInstallQueuePresentation();

    }



    public bool IsRefreshing

    {

        get => _isRefreshing;

        private set

        {

            if (SetProperty(ref _isRefreshing, value))

            {

                RaiseCommandStates();

            }

        }

    }



    public string SelectedCatalogPackageId

    {

        get => _selectedCatalogPackageId;

        set

        {

            if (SetProperty(ref _selectedCatalogPackageId, value))

            {

                InstallCommand.RaiseCanExecuteChanged();

            }

        }

    }



    public string StatusMessage

    {

        get => _statusMessage;

        set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                RaisePropertyChanged(nameof(HasStatusMessage));
            }
        }

    }

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);



    private void LoadCatalogPackages()

    {

        CatalogPhpPackages.Clear();

        var phpPackages = _catalogStore.List(PackageType.Php)

            .OrderByDescending(p => p.Id, StringComparer.OrdinalIgnoreCase)

            .ToList();



        foreach (var package in phpPackages)

        {

            CatalogPhpPackages.Add(package);

        }



        if (string.IsNullOrWhiteSpace(SelectedCatalogPackageId) && CatalogPhpPackages.Count > 0)

        {

            SelectedCatalogPackageId = CatalogPhpPackages[0].Id;

        }

    }



    public Task RefreshNowAsync() => RefreshAsync();



    private void OnInstallTrackerChanged()

    {

        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher is not null && !dispatcher.CheckAccess())

        {

            dispatcher.BeginInvoke(OnInstallTrackerChangedCore);

            return;

        }



        OnInstallTrackerChangedCore();

    }



    private void OnInstallTrackerChangedCore()

    {

        foreach (var item in _installTracker.Items.ToList())

        {

            if (!item.PackageId.StartsWith("php-", StringComparison.OrdinalIgnoreCase))

            {

                continue;

            }

            if (item.Phase == InstallPhase.Done && IsInstalledVersionListed(item.PackageId))

            {

                _installTracker.Dismiss(item.PackageId);

                continue;

            }

            if (item.Phase == InstallPhase.Done && !IsInstalledVersionListed(item.PackageId))

            {

                _ = AddInstalledVersionAsync(item.PackageId);

            }

        }

        RaiseCommandStates();

    }



    private async Task ApplyPhpConfigChangeAsync(string versionId)
    {
        await _activity.RunAsync(
            "PHP",
            "Saving PHP settings…",
            () => ApplyPhpConfigChangeWorkAsync(versionId),
            "PHP configuration updated. Web requests now use the new settings.").ConfigureAwait(true);
        StatusMessage = "PHP configuration updated. Web requests now use the new settings.";
    }

    private async Task ApplyPhpConfigChangeWorkAsync(string versionId)
    {
        await RefreshAsync();
        var result = await _serviceManager.RestartPhpFastCgiAsync([versionId]);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                result.Message ?? "PHP settings saved, but the web listener could not be restarted.");
        }
    }

    private async Task ApplyPhpRuntimeSettingsAsync()
    {
        await SettingsSaveFeedback.RunAsync(
            msg => StatusMessage = msg,
            "Saving PHP runtime settings…",
            "PHP runtime settings saved and applied.",
            ApplyPhpRuntimeSettingsWorkAsync).ConfigureAwait(true);
    }

    private async Task ApplyPhpRuntimeSettingsWorkAsync()
    {
        await RefreshAsync();
        var versionIds = ResolveRequiredVersionIds();
        if (versionIds.Count == 0)
        {
            return;
        }

        var result = await _serviceManager.RestartPhpFastCgiAsync(versionIds);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                result.Message ?? "Runtime settings saved, but FastCGI could not be restarted.");
        }
    }

    private async Task RefreshAsync()
    {
        if (_isRefreshing)

        {

            _refreshAgain = true;

            return;

        }



        IsRefreshing = true;

        try

        {

            do

            {

                _refreshAgain = false;

                await SyncInstalledVersionsAsync();

                StatusMessage = InstalledVersions.Count == 0

                    ? "No installed PHP versions found."

                    : "PHP versions refreshed.";

            }

            while (_refreshAgain);

        }

        finally

        {

            IsRefreshing = false;

        }

    }



    private async Task SyncInstalledVersionsAsync()

    {

        var settings = _settingsStore.Load();

        var requiredVersionIds = ResolveRequiredVersionIds();

        var phpVersions = _phpConfigWriter.ListInstalledPhpVersions(settings, requiredVersionIds);



        foreach (var stale in InstalledVersions

                     .Where(row => phpVersions.All(v => !string.Equals(v.Id, row.Id, StringComparison.OrdinalIgnoreCase)))

                     .ToList())

        {

            InstalledVersions.Remove(stale);

        }



        foreach (var version in phpVersions)

        {

            var isListening = version.FastCgiPort is int port

                && await _serviceManager.IsPortOpenAsync(settings.Php.FpmHost, port);



            var existing = InstalledVersions.FirstOrDefault(row =>

                string.Equals(row.Id, version.Id, StringComparison.OrdinalIgnoreCase));

            if (existing is null)

            {

                InstalledVersions.Add(CreateVersionRow(version, isListening));

                continue;

            }



            ApplyVersionRow(existing, version, isListening);

        }

    }



    private async Task AddInstalledVersionAsync(string packageId)

    {

        if (IsInstalledVersionListed(packageId))

        {

            _installTracker.Dismiss(packageId);

            UpdatePhpInstallQueuePresentation();

            return;

        }



        if (!_addingVersionIds.Add(packageId))

        {

            return;

        }



        try

        {

            if (!_registryStore.IsInstalled(packageId))

            {

                return;

            }



            var settings = _settingsStore.Load();

            _extensionManager.EnsureVersionSettings(packageId);

            _phpConfigWriter.WritePhpConfig(settings, packageId);



            settings = _settingsStore.Load();

            var requiredVersionIds = ResolveRequiredVersionIds();

            var version = _phpConfigWriter.ListInstalledPhpVersions(settings, requiredVersionIds)

                .FirstOrDefault(v => string.Equals(v.Id, packageId, StringComparison.OrdinalIgnoreCase));

            if (version is null)

            {

                return;

            }



            var isListening = version.FastCgiPort is int port

                && await _serviceManager.IsPortOpenAsync(settings.Php.FpmHost, port);



            var existing = InstalledVersions.FirstOrDefault(row =>

                string.Equals(row.Id, packageId, StringComparison.OrdinalIgnoreCase));

            if (existing is null)

            {

                InstalledVersions.Add(CreateVersionRow(version, isListening));

            }

            else

            {

            ApplyVersionRow(existing, version, isListening);

        }



        _installTracker.Dismiss(packageId);

        UpdatePhpInstallQueuePresentation();

    }

        finally

        {

            _addingVersionIds.Remove(packageId);

        }

    }



    private static PhpVersionRowViewModel CreateVersionRow(PhpVersionInfo version, bool isListening)

    {

        var row = new PhpVersionRowViewModel();

        ApplyVersionRow(row, version, isListening);

        return row;

    }



    private static void ApplyVersionRow(PhpVersionRowViewModel row, PhpVersionInfo version, bool isListening)

    {

        row.Id = version.Id;

        row.Version = version.Version;

        row.InstallPath = version.InstallPath;

        row.IsActive = version.IsActive;

        row.FastCgiPort = version.FastCgiPort;

        row.IsRequired = version.IsRequired;

        row.IsRunning = isListening;

    }



    private IReadOnlyList<string> ResolveRequiredVersionIds() =>

        _serviceManager.ResolveRequiredPhpVersionIds();



    private async Task UpdateListenerStatusesAsync()

    {

        var settings = _settingsStore.Load();

        foreach (var row in InstalledVersions)

        {

            if (row.FastCgiPort is not int port)

            {

                row.IsRunning = false;

                continue;

            }



            row.IsRunning = await _serviceManager.IsPortOpenAsync(settings.Php.FpmHost, port);

        }

    }



    private void InstallSelected()

    {

        if (!CanInstallSelected())

        {

            return;

        }



        var packageId = SelectedCatalogPackageId;

        var package = _catalogStore.GetById(packageId);

        if (package is null)

        {

            StatusMessage = $"Catalog package '{packageId}' not found.";

            return;

        }



        RaiseCommandStates();

        StatusMessage = $"Installing {package.Label} in background…";

        _ = InstallPackageInBackgroundAsync(package);

    }



    private async Task InstallPackageInBackgroundAsync(PackageEntry package)

    {

        try

        {

            await Task.Run(async () =>

                    await _packages.InstallAsync(package).ConfigureAwait(false))

                .ConfigureAwait(false);



            var dispatcher = Application.Current?.Dispatcher;

            if (dispatcher is null)

            {

                await AddInstalledVersionAsync(package.Id);

                StatusMessage = $"Installed {package.Label}.";
                _activity.LogSuccess("PHP", SessionActivityMessages.PackageInstalled(package.Label));

            }

            else

            {

                await dispatcher.InvokeAsync(async () =>

                {

                    await AddInstalledVersionAsync(package.Id);

                    StatusMessage = $"Installed {package.Label}.";
                _activity.LogSuccess("PHP", SessionActivityMessages.PackageInstalled(package.Label));

                }).Task.Unwrap();

            }



        }

        catch (Exception ex)

        {

            await Application.Current.Dispatcher.InvokeAsync(() =>

            {

                StatusMessage = ex.Message;

                _installTracker.Dismiss(package.Id);

                UpdatePhpInstallQueuePresentation();

            });

        }

        finally

        {

            await Application.Current.Dispatcher.InvokeAsync(RaiseCommandStates);

        }

    }



    private async Task EnsureFastCgiAsync()

    {

        if (IsBusy)

        {

            return;

        }



        IsRefreshing = true;

        try

        {

            var result = await _serviceManager.EnsureRequiredPhpFastCgiAsync();

            StatusMessage = result.Success

                ? "Required PHP FastCGI listeners are running."

                : (result.Message ?? "Failed to start required PHP FastCGI.");

        }

        finally

        {

            IsRefreshing = false;

            await UpdateListenerStatusesAsync();

        }

    }



    private async Task SetActiveVersionAsync(string? versionId)

    {

        if (IsBusy || string.IsNullOrWhiteSpace(versionId))

        {

            return;

        }



        IsRefreshing = true;

        try

        {

            var settings = _settingsStore.Load();

            settings.Php.ActiveVersionId = versionId;

            _settingsStore.UpdatePhp(settings.Php);

            _phpConfigWriter.WritePhpConfig(settings, versionId);



            foreach (var row in InstalledVersions)

            {

                row.IsActive = string.Equals(row.Id, versionId, StringComparison.OrdinalIgnoreCase);

            }



            StatusMessage = $"Active PHP set to {versionId}.";

            _activity.LogSuccess("PHP", SessionActivityMessages.PhpVersionActivated(versionId));



            await _binManager.SyncStackrootBinAsync();

            var result = await _serviceManager.EnsureRequiredPhpFastCgiAsync();



            if (!result.Success && !string.IsNullOrWhiteSpace(result.Message))

            {

                StatusMessage = result.Message;

            }



            await RefreshAsync();

        }

        finally

        {

            IsRefreshing = false;

        }

    }



    private async Task UninstallVersionAsync(string? versionId)

    {

        if (IsBusy || string.IsNullOrWhiteSpace(versionId) || IsPackageInstalling(versionId))

        {

            return;

        }



        var row = InstalledVersions.FirstOrDefault(v =>

            string.Equals(v.Id, versionId, StringComparison.OrdinalIgnoreCase));

        if (row is { IsRemoving: true })

        {

            return;

        }



        var confirmed = ConfirmDialog.Show(

            Application.Current?.MainWindow,

            "Uninstall PHP",

            $"Remove {versionId} and delete its files from disk?",

            "Uninstall",

            isDanger: true);

        if (!confirmed)

        {

            return;

        }



        var package = _catalogStore.GetById(versionId);

        if (package is null)

        {

            StatusMessage = $"Catalog package '{versionId}' not found.";

            return;

        }



        row?.SetRemoving(true);

        RaiseCommandStates();

        StatusMessage = $"Removing {versionId}…";

        try

        {

            await Task.Run(async () => await _packages.UninstallAsync(package).ConfigureAwait(false))

                .ConfigureAwait(true);



            var settings = _settingsStore.Load();

            if (string.Equals(settings.Php.ActiveVersionId, versionId, StringComparison.OrdinalIgnoreCase))

            {

                var remaining = _registryStore.List(PackageType.Php)

                    .OrderByDescending(p => p.Id, StringComparer.OrdinalIgnoreCase)

                    .FirstOrDefault();

                settings.Php.ActiveVersionId = remaining?.Id;

                _settingsStore.UpdatePhp(settings.Php);

            }



            if (settings.Php.Versions?.ContainsKey(versionId) == true)

            {

                var versions = new Dictionary<string, PhpVersionSettings>(settings.Php.Versions);

                versions.Remove(versionId);

                _settingsStore.UpdatePhp(settings.Php with { Versions = versions });

            }



            StatusMessage = $"Uninstalled {versionId}.";
            _activity.LogSuccess("PHP", SessionActivityMessages.PhpVersionUninstalled(versionId));

            await _nginxWebStackRebuilder.RebuildAsync();

        }

        catch (Exception ex)

        {

            StatusMessage = ex.Message;

        }

        finally

        {

            row?.SetRemoving(false);

            RaiseCommandStates();

            await RefreshAsync();

        }

    }



    private void OpenRuntimeSettingsDialog()

    {

        var dialogVm = new PhpRuntimeSettingsDialogViewModel(_extensionManager, _settingsStore);

        ShowDialog(new PhpRuntimeSettingsDialog { DataContext = dialogVm }, dialogVm);

    }



    private void OpenVersionSettingsDialog(string? versionId)

    {

        if (string.IsNullOrWhiteSpace(versionId))

        {

            return;

        }



        var row = InstalledVersions.FirstOrDefault(v => string.Equals(v.Id, versionId, StringComparison.OrdinalIgnoreCase));

        var label = row?.Id ?? versionId;

        var dialogVm = new PhpVersionSettingsDialogViewModel(

            _extensionManager,

            _phpConfigWriter,

            _settingsStore,

            versionId,

            label);

        ShowDialog(new PhpVersionSettingsDialog { DataContext = dialogVm }, dialogVm);

    }



    private void OpenExtensionsDialog(string? versionId)

    {

        if (string.IsNullOrWhiteSpace(versionId))

        {

            return;

        }



        var row = InstalledVersions.FirstOrDefault(v => string.Equals(v.Id, versionId, StringComparison.OrdinalIgnoreCase));

        var label = row?.Id ?? versionId;

        var dialogVm = new PhpExtensionsDialogViewModel(_extensionManager, _peclInstaller, versionId, label);

        ShowDialog(new PhpExtensionsDialog { DataContext = dialogVm }, dialogVm, refreshOnChange: true);

    }



    private void ShowDialog(Window dialog, ViewModelBase dialogVm, bool refreshOnChange = false)

    {

        dialog.Owner = Application.Current?.MainWindow;

        SettingsSaveFeedback.DeferredSettingsSave? deferred = null;

        if (dialogVm is PhpRuntimeSettingsDialogViewModel runtimeVm)

        {

            runtimeVm.RequestClose += (_, _) => dialog.Close();

            runtimeVm.SettingsSaved += (_, _) =>
            {
                deferred = new SettingsSaveFeedback.DeferredSettingsSave(
                    "Saving PHP runtime settings…",
                    runtimeVm.StatusMessage,
                    ApplyPhpRuntimeSettingsWorkAsync);
            };

        }

        else if (dialogVm is PhpVersionSettingsDialogViewModel versionVm)

        {

            versionVm.RequestClose += (_, _) => dialog.Close();

            versionVm.SettingsSaved += (_, _) =>
            {
                var versionId = versionVm.VersionId;
                deferred = new SettingsSaveFeedback.DeferredSettingsSave(
                    "Saving PHP settings…",
                    "PHP configuration updated. Web requests now use the new settings.",
                    () => ApplyPhpConfigChangeWorkAsync(versionId));
            };

        }

        else if (dialogVm is PhpExtensionsDialogViewModel extensionsVm)

        {

            extensionsVm.RequestClose += (_, _) => dialog.Close();

            if (refreshOnChange)
            {
                extensionsVm.SettingsChanged += async (_, _) =>
                {
                    if (!string.IsNullOrWhiteSpace(extensionsVm.StatusMessage))
                    {
                        _activity.LogSuccess("PHP", extensionsVm.StatusMessage);
                    }

                    await ApplyPhpConfigChangeAsync(extensionsVm.VersionId);
                };
            }

        }



        dialog.ShowDialog();

        if (deferred is { } save)
        {
            _ = SettingsSaveFeedback.RunDeferredOnSessionActivityAsync(_activity, save);
        }

    }

}



public sealed class PhpVersionRowViewModel : ViewModelBase

{

    private string _id = string.Empty;

    private string _version = string.Empty;

    private string _installPath = string.Empty;

    private bool _isActive;

    private int? _fastCgiPort;

    private bool _isRunning;

    private bool _isRequired = true;

    private bool _isRemoving;



    public string Id

    {

        get => _id;

        set => SetProperty(ref _id, value);

    }



    public string Version

    {

        get => _version;

        set => SetProperty(ref _version, value);

    }



    public string InstallPath

    {

        get => _installPath;

        set => SetProperty(ref _installPath, value);

    }



    public bool IsActive

    {

        get => _isActive;

        set

        {

            if (SetProperty(ref _isActive, value))

            {

                RaisePresentationChanged();

            }

        }

    }



    public int? FastCgiPort

    {

        get => _fastCgiPort;

        set

        {

            if (SetProperty(ref _fastCgiPort, value))

            {

                RaisePresentationChanged();

            }

        }

    }



    public bool IsRequired

    {

        get => _isRequired;

        set

        {

            if (SetProperty(ref _isRequired, value))

            {

                RaisePresentationChanged();

            }

        }

    }



    public bool IsRunning

    {

        get => _isRunning;

        set

        {

            if (SetProperty(ref _isRunning, value))

            {

                RaisePresentationChanged();

            }

        }

    }



    public bool IsRemoving

    {

        get => _isRemoving;

        private set

        {

            if (SetProperty(ref _isRemoving, value))

            {

                RaisePresentationChanged();

            }

        }

    }



    public void SetRemoving(bool removing) => IsRemoving = removing;



    public bool ShowRemovingProgress => IsRemoving;

    public bool CanInteract => !IsRemoving;

    public string UninstallButtonLabel => IsRemoving ? "Removing…" : "Uninstall";



    public string FastCgiSummary =>

        IsRemoving

            ? "Removing…"

            : FastCgiPort is int port

                ? IsRequired

                    ? $":{port} · {(IsRunning ? "Running" : "Stopped")}"

                    : $":{port} · Optional"

                : "Not configured";



    public System.Windows.Media.Brush StatusForegroundBrush => CreateBrush(

        IsRemoving ? "#E9BD5B" :

        IsRunning ? "#8FD6B6" : "#91A0B5");



    public System.Windows.Media.Brush StatusBackgroundBrush => CreateBrush(

        IsRemoving ? "#33E9BD5B" :

        IsRunning ? "#1A4CAE8C" : "#1A263348");



    public bool ShowActiveBadge => IsActive && !IsRemoving;



    public string ActiveButtonLabel => IsActive ? "Active" : "Set Active";



    public bool CanSetActive => !IsActive && !IsRemoving;



    public System.Windows.Media.Brush RowBorderBrush => CreateBrush(

        IsRemoving ? "#E9BD5B" :

        IsActive ? "#4CAE8C" : "#263348");



    private void RaisePresentationChanged()

    {

        RaisePropertyChanged(nameof(FastCgiSummary));

        RaisePropertyChanged(nameof(StatusForegroundBrush));

        RaisePropertyChanged(nameof(StatusBackgroundBrush));

        RaisePropertyChanged(nameof(ShowActiveBadge));

        RaisePropertyChanged(nameof(ActiveButtonLabel));

        RaisePropertyChanged(nameof(CanSetActive));

        RaisePropertyChanged(nameof(RowBorderBrush));

        RaisePropertyChanged(nameof(ShowRemovingProgress));

        RaisePropertyChanged(nameof(CanInteract));

        RaisePropertyChanged(nameof(UninstallButtonLabel));

    }



    private static System.Windows.Media.SolidColorBrush CreateBrush(string hex) =>

        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);

}


