using System.Collections.ObjectModel;
using System.Windows;
using Stackroot.App.Commands;
using Stackroot.App.Helpers;
using Stackroot.App.Services;
using Stackroot.App.Views;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Node;
using Stackroot.Core.Settings;
using Stackroot.Core.Windows;

namespace Stackroot.App.ViewModels;

public sealed class NodeViewModel : ViewModelBase
{
    private readonly NodeManager _nodeManager;
    private readonly NodeVersionCatalog _versionCatalog;
    private readonly StackrootBinManager _binManager;
    private readonly SettingsStore _settingsStore;
    private readonly PackageCatalogStore _catalogStore;
    private readonly PackageInstallCoordinator _packages;
    private readonly SessionActivityReporter _activity;
    private CancellationTokenSource? _suggestionsRefreshCts;
    private bool _isBusy;
    private bool _nvmInstalled;
    private string? _selectedInstallVersion;
    private string _customVersionInput = string.Empty;
    private string? _activeVersion;
    private string _nvmVersionLabel = "Not installed";
    private string? _nvmPackageLabel;
    private string? _nodeExecutablePath;
    private string? _statusMessage;
    private bool _statusIsError;
    private string? _latestNvmCatalogVersion;
    private int _lazyInitStarted;

    public NodeViewModel(
        NodeManager nodeManager,
        NodeVersionCatalog versionCatalog,
        StackrootBinManager binManager,
        SettingsStore settingsStore,
        PackageCatalogStore catalogStore,
        PackageInstallCoordinator packages,
        SessionActivityReporter activity)
    {
        _nodeManager = nodeManager;
        _versionCatalog = versionCatalog;
        _binManager = binManager;
        _settingsStore = settingsStore;
        _catalogStore = catalogStore;
        _packages = packages;
        _activity = activity;

        InstalledVersions = [];
        InstallVersionOptions = [];
        RecommendedVersions = new ObservableCollection<string>(_versionCatalog.GetRecommendedVersions());

        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(), _ => !IsBusy);
        InstallNvmCommand = new RelayCommand(_ => _ = InstallNvmAsync(), _ => !IsBusy && !NvmInstalled);
        UpdateNvmCommand = new RelayCommand(_ => _ = UpdateNvmAsync(), _ => !IsBusy && NvmInstalled && CanUpdateNvm);
        UninstallNvmCommand = new RelayCommand(_ => _ = UninstallNvmAsync(), _ => !IsBusy && NvmInstalled);
        InstallNodeCommand = new RelayCommand(_ => _ = InstallNodeAsync(), _ => CanInstallNode);
        InstallRecommendedCommand = new RelayCommand(
            arg => _ = InstallRecommendedAsync(arg as string),
            _ => !IsBusy && NvmInstalled);
        ActivateCommand = new RelayCommand(
            arg => _ = ActivateAsync(arg as string),
            arg => !IsBusy && NvmInstalled && arg is string);
        UninstallNodeCommand = new RelayCommand(
            arg => _ = UninstallNodeAsync(arg as string),
            arg => !IsBusy && NvmInstalled && arg is string version && !IsVersionRemoving(version));
        OpenSettingsCommand = new RelayCommand(_ => OpenSettingsDialog(), _ => !IsBusy);
        DismissStatusMessageCommand = new RelayCommand(_ => SetStatus(null, isError: false));

        LoadLatestNvmCatalogVersion();
    }

    /// <summary>Warm page data after shell startup — safe to call from UI thread.</summary>
    public void BeginLoading()
    {
        if (Interlocked.CompareExchange(ref _lazyInitStarted, 1, 0) == 0)
        {
            _ = InitializeAsync();
        }
    }

    public ObservableCollection<NodeVersionRowViewModel> InstalledVersions { get; }
    public ObservableCollection<string> InstallVersionOptions { get; }
    public ObservableCollection<string> RecommendedVersions { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand InstallNvmCommand { get; }
    public RelayCommand UpdateNvmCommand { get; }
    public RelayCommand UninstallNvmCommand { get; }
    public RelayCommand InstallNodeCommand { get; }
    public RelayCommand InstallRecommendedCommand { get; }
    public RelayCommand ActivateCommand { get; }
    public RelayCommand UninstallNodeCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand DismissStatusMessageCommand { get; }

    public bool NvmInstalled => _nvmInstalled;
    public bool ShowNvmSetup => !NvmInstalled;
    public bool ShowNodeSections => NvmInstalled;

    public bool CanUpdateNvm =>
        NvmInstalled
        && !string.IsNullOrWhiteSpace(_latestNvmCatalogVersion)
        && !string.Equals(_latestNvmCatalogVersion, InstalledNvmPackageVersion, StringComparison.OrdinalIgnoreCase);

    public string? InstalledNvmPackageVersion => _nodeManager.GetInstalledNvmPackage()?.Version;

    public string ActiveNodeLabel => string.IsNullOrWhiteSpace(ActiveVersion) ? "(none)" : ActiveVersion!;

    public string? ActiveVersion
    {
        get => _activeVersion;
        private set
        {
            if (SetProperty(ref _activeVersion, value))
            {
                RaisePropertyChanged(nameof(ActiveNodeLabel));
            }
        }
    }

    public string NvmVersionLabel
    {
        get => _nvmVersionLabel;
        private set => SetProperty(ref _nvmVersionLabel, value);
    }

    public string? NvmPackageLabel
    {
        get => _nvmPackageLabel;
        private set => SetProperty(ref _nvmPackageLabel, value);
    }

    public string? NodeExecutablePath
    {
        get => _nodeExecutablePath;
        private set => SetProperty(ref _nodeExecutablePath, value);
    }

    public string UpdateNvmLabel =>
        string.IsNullOrWhiteSpace(_latestNvmCatalogVersion)
            ? "Update nvm"
            : $"Update to {_latestNvmCatalogVersion}";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string? SelectedInstallVersion
    {
        get => _selectedInstallVersion;
        set
        {
            if (SetProperty(ref _selectedInstallVersion, value))
            {
                InstallNodeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string CustomVersionInput
    {
        get => _customVersionInput;
        set
        {
            if (SetProperty(ref _customVersionInput, value ?? string.Empty))
            {
                if (!string.IsNullOrWhiteSpace(value))
                    SelectedInstallVersion = null; // clear dropdown so user sees custom input takes priority
                InstallNodeCommand.RaiseCanExecuteChanged();
                ScheduleSuggestionRefresh();
            }
        }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                RaisePropertyChanged(nameof(HasStatusMessage));
            }
        }
    }

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public bool StatusIsError
    {
        get => _statusIsError;
        private set => SetProperty(ref _statusIsError, value);
    }

    public bool HasInstalledVersions => InstalledVersions.Count > 0;
    public bool ShowEmptyInstalledMessage => !HasInstalledVersions;
    public bool ShowNvmPackageLabel => !string.IsNullOrWhiteSpace(NvmPackageLabel);

    private bool CanInstallNode =>
        !IsBusy && NvmInstalled && !string.IsNullOrWhiteSpace(ResolveInstallTarget());

    private async Task InitializeAsync()
    {
        await UpdateSuggestionsAsync();
        await RefreshAsync();
    }

    private void LoadLatestNvmCatalogVersion()
    {
        _latestNvmCatalogVersion = _catalogStore.List(PackageType.Nvm)
            .OrderByDescending(package => package.Version, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()
            ?.Version;
        RaisePropertyChanged(nameof(UpdateNvmLabel));
        RaisePropertyChanged(nameof(CanUpdateNvm));
        UpdateNvmCommand.RaiseCanExecuteChanged();
    }

    private void ScheduleSuggestionRefresh()
    {
        _suggestionsRefreshCts?.Cancel();
        _suggestionsRefreshCts?.Dispose();
        _suggestionsRefreshCts = new CancellationTokenSource();
        _ = RefreshSuggestionsDebouncedAsync(_suggestionsRefreshCts.Token);
    }

    private async Task RefreshSuggestionsDebouncedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(200, cancellationToken);
            await UpdateSuggestionsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task UpdateSuggestionsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var filter = string.IsNullOrWhiteSpace(CustomVersionInput) ? SelectedInstallVersion : CustomVersionInput;
            var suggestions = await _versionCatalog.BuildSuggestionsAsync(filter, cancellationToken);
            VersionSuggestionsUpdater.Apply(InstallVersionOptions, suggestions);

            if (!string.IsNullOrWhiteSpace(SelectedInstallVersion)
                && !InstallVersionOptions.Any(version =>
                    string.Equals(version, SelectedInstallVersion, StringComparison.OrdinalIgnoreCase)))
            {
                SelectedInstallVersion = InstallVersionOptions.FirstOrDefault();
            }
            else if (string.IsNullOrWhiteSpace(SelectedInstallVersion) && string.IsNullOrWhiteSpace(CustomVersionInput))
            {
                SelectedInstallVersion = InstallVersionOptions.FirstOrDefault();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
        }
    }

    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        SetStatus("Refreshing Node runtime…", isError: false);
        try
        {
            LoadLatestNvmCatalogVersion();
            var status = await _nodeManager.GetRuntimeStatusAsync();
            ApplyStatus(status);
            SetStatus(status.Message ?? "Node runtime is up to date.", isError: false);
            await UpdateSuggestionsAsync();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
            _activity.LogError("Node", ex.Message, ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallNvmAsync() =>
        await InstallNvmPackageAsync("Installing nvm-windows…", "nvm-windows installed.");

    private async Task UpdateNvmAsync() =>
        await InstallNvmPackageAsync($"Updating nvm-windows to {_latestNvmCatalogVersion}…", "nvm-windows updated.");

    private async Task InstallNvmPackageAsync(string busyMessage, string successMessage)
    {
        if (IsBusy)
        {
            return;
        }

        var entry = ResolveLatestNvmCatalogEntry();
        if (entry is null)
        {
            SetStatus("nvm-windows package was not found in the catalog.", isError: true);
            return;
        }

        IsBusy = true;
        SetStatus(busyMessage, isError: false);
        try
        {
            await _packages.InstallAsync(entry);
            var settings = _settingsStore.Load();
            _settingsStore.UpdateNode(settings.Node with { NvmPackageId = entry.Id });
            await _binManager.SyncStackrootBinAsync();
            ApplyStatus(await _nodeManager.GetRuntimeStatusAsync());
            SetStatus(successMessage, isError: false);
            _activity.LogSuccess("Node", successMessage);
            await UpdateSuggestionsAsync();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
            _activity.LogError("Node", ex.Message, ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task UninstallNvmAsync()
    {
        if (IsBusy || !NvmInstalled)
        {
            return;
        }

        var package = ResolveInstalledNvmEntry();
        if (package is null)
        {
            SetStatus("Installed nvm package could not be resolved.", isError: true);
            return;
        }

        if (!ConfirmDialog.Show(
                Application.Current?.MainWindow,
                "Remove nvm-windows?",
                "This removes the Stackroot-managed nvm package. Node version folders may remain on disk.",
                "Remove",
                isDanger: true))
        {
            return;
        }

        IsBusy = true;
        SetStatus("Removing nvm-windows…", isError: false);
        try
        {
            await _packages.UninstallAsync(package);
            _nodeManager.ClearNvmSettings();
            await _binManager.SyncStackrootBinAsync();
            ApplyStatus(await _nodeManager.GetRuntimeStatusAsync());
            SetStatus("nvm-windows removed.", isError: false);
            _activity.LogSuccess("Node", SessionActivityMessages.PackageUninstalled("nvm-windows"));
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
            _activity.LogError("Node", ex.Message, ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallRecommendedAsync(string? version)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(version))
        {
            return;
        }

        SelectedInstallVersion = version;
        CustomVersionInput = string.Empty;
        await InstallNodeAsync(version);
    }

    private Task InstallNodeAsync() => InstallNodeAsync(ResolveInstallTarget());

    private async Task InstallNodeAsync(string? target)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        var normalized = NodePaths.NormalizeVersion(target.Trim());
        IsBusy = true;
        SetStatus($"Installing Node {normalized}…", isError: false);
        try
        {
            var status = await _nodeManager.InstallVersionAsync(normalized);
            await _binManager.SyncStackrootBinAsync();
            ApplyStatus(status);
            CustomVersionInput = string.Empty;
            var installedVersion = status.ActiveVersion ?? normalized;
            SelectedInstallVersion = installedVersion;
            SetStatus($"Installed and activated Node {installedVersion}.", isError: false);
            _activity.LogSuccess("Node", SessionActivityMessages.NodeVersionInstalled(installedVersion));
            await UpdateSuggestionsAsync();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
            _activity.LogError("Node", ex.Message, ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ActivateAsync(string? version)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(version))
        {
            return;
        }

        IsBusy = true;
        SetStatus($"Activating Node {version}…", isError: false);
        try
        {
            var status = await _nodeManager.UseVersionAsync(version);
            await _binManager.SyncStackrootBinAsync();
            ApplyStatus(status);
            SetStatus($"Active Node is now {version}.", isError: false);
            _activity.LogSuccess("Node", SessionActivityMessages.NodeVersionActivated(version));
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
            _activity.LogError("Node", ex.Message, ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task UninstallNodeAsync(string? version)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(version))
        {
            return;
        }

        if (!ConfirmDialog.Show(
                Application.Current?.MainWindow,
                "Remove Node version?",
                $"Remove Node {version} from nvm?",
                "Remove",
                isDanger: true))
        {
            return;
        }

        var row = InstalledVersions.FirstOrDefault(item =>
            string.Equals(item.Version, version, StringComparison.OrdinalIgnoreCase));
        row?.SetRemoving(true);

        IsBusy = true;
        SetStatus($"Removing Node {version}…", isError: false);
        try
        {
            var status = await _nodeManager.UninstallVersionAsync(version);
            await _binManager.SyncStackrootBinAsync();
            ApplyStatus(status);
            SetStatus($"Removed Node {version}.", isError: false);
            _activity.LogSuccess("Node", SessionActivityMessages.NodeVersionUninstalled(version));
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
            _activity.LogError("Node", ex.Message, ex);
        }
        finally
        {
            row?.SetRemoving(false);
            IsBusy = false;
        }
    }

    private void OpenSettingsDialog()
    {
        var dialogVm = new NodeSettingsDialogViewModel(_settingsStore);
        var dialog = new NodeSettingsDialog
        {
            DataContext = dialogVm,
            Owner = Application.Current?.MainWindow
        };

        dialogVm.RequestClose += (_, _) => dialog.Close();

        SettingsSaveFeedback.DeferredSettingsSave? deferred = null;
        dialogVm.SettingsSaved += (_, _) =>
        {
            deferred = new SettingsSaveFeedback.DeferredSettingsSave(
                "Saving Node settings…",
                dialogVm.StatusMessage);
        };

        dialog.ShowDialog();

        if (deferred is { } save)
        {
            _ = SettingsSaveFeedback.RunDeferredOnSessionActivityAsync(_activity, save);
        }
    }

    private void ApplyStatus(NodeRuntimeStatus status)
    {
        _nvmInstalled = status.NvmInstalled;
        NvmVersionLabel = status.NvmInstalled
            ? $"nvm {status.NvmVersion ?? "ready"}"
            : "Not installed";

        var installedPackage = _nodeManager.GetInstalledNvmPackage();
        NvmPackageLabel = installedPackage is null
            ? null
            : $"{installedPackage.Id} · v{installedPackage.Version}";
        RaisePropertyChanged(nameof(ShowNvmPackageLabel));

        ActiveVersion = status.ActiveVersion;
        NodeExecutablePath = status.NodeExecutablePath;
        SyncInstalledRows(status.InstalledVersions, status.ActiveVersion);

        RaisePropertyChanged(nameof(NvmInstalled));
        RaisePropertyChanged(nameof(ShowNvmSetup));
        RaisePropertyChanged(nameof(ShowNodeSections));
        RaisePropertyChanged(nameof(CanUpdateNvm));
        RaisePropertyChanged(nameof(InstalledNvmPackageVersion));
        RaisePropertyChanged(nameof(HasInstalledVersions));
        RaisePropertyChanged(nameof(ShowEmptyInstalledMessage));
        UpdateNvmCommand.RaiseCanExecuteChanged();
        InstallNvmCommand.RaiseCanExecuteChanged();
        UninstallNvmCommand.RaiseCanExecuteChanged();
        InstallNodeCommand.RaiseCanExecuteChanged();
        InstallRecommendedCommand.RaiseCanExecuteChanged();
        ActivateCommand.RaiseCanExecuteChanged();
        UninstallNodeCommand.RaiseCanExecuteChanged();
    }

    private void SyncInstalledRows(IReadOnlyList<string> versions, string? activeVersion)
    {
        for (var index = InstalledVersions.Count - 1; index >= 0; index--)
        {
            if (!versions.Any(version =>
                    string.Equals(version, InstalledVersions[index].Version, StringComparison.OrdinalIgnoreCase)))
            {
                InstalledVersions.RemoveAt(index);
            }
        }

        foreach (var version in versions)
        {
            var row = InstalledVersions.FirstOrDefault(item =>
                string.Equals(item.Version, version, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                InstalledVersions.Add(new NodeVersionRowViewModel
                {
                    Version = version,
                    IsActive = string.Equals(version, activeVersion, StringComparison.OrdinalIgnoreCase)
                });
            }
            else
            {
                row.IsActive = string.Equals(version, activeVersion, StringComparison.OrdinalIgnoreCase);
            }
        }

        var ordered = InstalledVersions.OrderByDescending(row => row.Version, StringComparer.OrdinalIgnoreCase).ToList();
        InstalledVersions.Clear();
        foreach (var row in ordered)
        {
            InstalledVersions.Add(row);
        }

        RaisePropertyChanged(nameof(HasInstalledVersions));
        RaisePropertyChanged(nameof(ShowEmptyInstalledMessage));
    }

    private string? ResolveInstallTarget()
    {
        if (!string.IsNullOrWhiteSpace(CustomVersionInput))
        {
            return CustomVersionInput.Trim();
        }

        return SelectedInstallVersion;
    }

    private PackageEntry? ResolveLatestNvmCatalogEntry() =>
        _catalogStore.List(PackageType.Nvm)
            .OrderByDescending(package => package.Version, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private PackageEntry? ResolveInstalledNvmEntry()
    {
        var installed = _nodeManager.GetInstalledNvmPackage();
        if (installed is null)
        {
            return null;
        }

        return _catalogStore.GetById(installed.Id)
            ?? new PackageEntry
            {
                Id = installed.Id,
                Type = PackageType.Nvm,
                Version = installed.Version,
                InstallDir = installed.InstallPath
            };
    }

    private bool IsVersionRemoving(string version) =>
        InstalledVersions.FirstOrDefault(row => string.Equals(row.Version, version, StringComparison.OrdinalIgnoreCase))
            is { IsRemoving: true };

    private void SetStatus(string? message, bool isError)
    {
        StatusMessage = message;
        StatusIsError = isError;
    }

    private void RaiseCommandStates()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        InstallNvmCommand.RaiseCanExecuteChanged();
        UpdateNvmCommand.RaiseCanExecuteChanged();
        UninstallNvmCommand.RaiseCanExecuteChanged();
        InstallNodeCommand.RaiseCanExecuteChanged();
        InstallRecommendedCommand.RaiseCanExecuteChanged();
        ActivateCommand.RaiseCanExecuteChanged();
        UninstallNodeCommand.RaiseCanExecuteChanged();
        OpenSettingsCommand.RaiseCanExecuteChanged();
        RaisePropertyChanged(nameof(IsBusy));
    }
}
