using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Stackroot.App.Helpers;
using Stackroot.App.Commands;
using Stackroot.App.Services;
using Stackroot.App.Views;
using Stackroot.Core.Abstractions;
using Stackroot.Core.AdminTools;
using Stackroot.Core.Catalog;
using Stackroot.Core.Node;
using Stackroot.Core.Settings;
using Stackroot.Core.Services;
using Stackroot.Core.Services.Php;

namespace Stackroot.App.ViewModels;

public sealed class ToolsViewModel : ViewModelBase
{
    private const string VersionsHint =
        "Choose one version, then click Install. The dialog closes and progress appears in the tray (↓).";

    private readonly PhpMyAdminManager _phpMyAdminManager;
    private readonly PhpRedisAdminManager _phpRedisAdminManager;
    private readonly SettingsStore _settingsStore;
    private readonly InstallRegistryStore _registryStore;
    private readonly PackageCatalogStore _catalogStore;
    private readonly PackageInstallCoordinator _packages;
    private readonly InstallProgressTracker _installTracker;
    private readonly AppDomainConfigWriter _appDomainConfigWriter;
    private readonly StackrootPaths _paths;
    private readonly PhpViewModel _phpViewModel;
    private readonly PhpConfigWriter _phpConfigWriter;
    private readonly NginxWebStackRebuilder _nginxWebStackRebuilder;
    private readonly DashboardViewModel _dashboardViewModel;
    private readonly SessionActivityReporter _activity;

    private bool _phpMyAdminInstalled;
    private bool _phpMyAdminCanOpen;
    private bool _phpMyAdminShowInstallButton = true;
    private bool _phpMyAdminShowVersionsButton;
    private bool _phpMyAdminIsBusy;
    private string _phpMyAdminStatus = "Unknown";
    private string _phpMyAdminUrl = string.Empty;
    private string _phpMyAdminActivePackageLabel = "—";
    private string _phpMyAdminPhpVersionBadge = string.Empty;
    private string? _selectedPhpMyAdminPhpVersionId;
    private bool _suppressPhpMyAdminPhpVersionPersist;
    private bool _hasInstalledPhp;

    private bool _phpRedisAdminEnabled;
    private bool _phpRedisAdminInstalled;
    private string _phpRedisAdminStatus = "Unknown";
    private string _phpRedisAdminMessage = string.Empty;
    private string _phpRedisAdminUrl = string.Empty;
    private string _phpRedisAdminOpenLabel = string.Empty;
    private string _phpRedisAdminPhpVersionBadge = string.Empty;
    private string _phpRedisAdminActivePackageLabel = string.Empty;
    private bool _phpRedisAdminCanOpen;
    private bool _phpRedisAdminShowInstallButton;
    private bool _phpRedisAdminShowVersionsButton;
    private bool _phpRedisAdminIsBusy;
    private bool _suppressPhpRedisAdminEnabledPersist;

    private readonly AdminUiToolViewModel _phpMyAdminAdminRow;
    private readonly AdminUiToolViewModel _phpRedisAdminAdminRow;

    private string? _message;

    public ToolsViewModel(
        PhpMyAdminManager phpMyAdminManager,
        PhpRedisAdminManager phpRedisAdminManager,
        SettingsStore settingsStore,
        InstallRegistryStore registryStore,
        PackageCatalogStore catalogStore,
        PackageInstallCoordinator packages,
        InstallProgressTracker installTracker,
        AppDomainConfigWriter appDomainConfigWriter,
        StackrootPaths paths,
        PhpViewModel phpViewModel,
        PhpConfigWriter phpConfigWriter,
        NginxWebStackRebuilder nginxWebStackRebuilder,
        DashboardViewModel dashboardViewModel,
        SessionActivityReporter activity)
    {
        _phpMyAdminManager = phpMyAdminManager;
        _phpRedisAdminManager = phpRedisAdminManager;
        _settingsStore = settingsStore;
        _registryStore = registryStore;
        _catalogStore = catalogStore;
        _packages = packages;
        _installTracker = installTracker;
        _appDomainConfigWriter = appDomainConfigWriter;
        _paths = paths;
        _phpViewModel = phpViewModel;
        _phpConfigWriter = phpConfigWriter;
        _nginxWebStackRebuilder = nginxWebStackRebuilder;
        _dashboardViewModel = dashboardViewModel;
        _activity = activity;
        _installTracker.Changed += (_, _) => RefreshCliToolInstallStates();

        var phpGroup = new ToolGroupViewModel { Title = "PHP" };
        phpGroup.Items.Add(CreateCliTool(PackageType.Composer, "Composer", "PHP dependency manager — requires active PHP"));
        phpGroup.Items.Add(CreateCliTool(PackageType.WpCli, "WP-CLI", "WordPress command-line interface — auto-installed when creating WordPress sites"));
        phpGroup.Items.Add(CreateCliTool(PackageType.Laravel, "Laravel Installer", "Composer CLI for laravel new — not the Laravel framework (currently v13)"));

        var jsGroup = new ToolGroupViewModel { Title = "JavaScript" };
        jsGroup.Items.Add(CreateCliTool(PackageType.Pnpm, "pnpm", "Node package manager — requires nvm-windows and an active Node version"));
        jsGroup.Items.Add(CreateCliTool(PackageType.Vite, "Vite", "Frontend dev server — requires nvm-windows and an active Node version (20.19+ or 22.12+)"));

        var utilGroup = new ToolGroupViewModel { Title = "Utilities" };
        utilGroup.Items.Add(CreateCliTool(PackageType.Git, "Git"));
        utilGroup.Items.Add(CreateCliTool(PackageType.Python, "Python"));
        utilGroup.Items.Add(CreateCliTool(PackageType.Sqlite, "SQLite CLI"));
        utilGroup.Items.Add(CreateCliTool(PackageType.Notepadpp, "Notepad++", "Portable editor — used when Preferred editor is Notepad++"));

        var dbGroup = new ToolGroupViewModel { Title = "Database" };
        dbGroup.Items.Add(CreateCliTool(PackageType.Mongosh, "MongoDB Shell", "mongosh — required for MongoDB database delete"));
        dbGroup.Items.Add(CreateCliTool(PackageType.MongodbTools, "MongoDB Tools", "mongodump / mongorestore — required for MongoDB backup/restore"));

        Groups = [phpGroup, jsGroup, utilGroup, dbGroup];

        PhpMyAdminPhpVersions =
        [
            new CompatiblePhpOption { Id = string.Empty, Label = "Auto — compatible PHP", Compatible = true }
        ];

        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync());
        DismissMessageCommand = new RelayCommand(_ => Message = null);
        CheckUpdatesCommand = new RelayCommand(_ => _ = CheckUpdatesAsync());
        DismissUpdatesCommand = new RelayCommand(_ => { UpdatesMessage = null; RaisePropertyChanged(nameof(UpdatesMessage)); RaisePropertyChanged(nameof(HasUpdates)); });

        OpenPhpMyAdminCommand = new RelayCommand(_ => OpenPhpMyAdmin(), _ => PhpMyAdminCanOpen && !PhpMyAdminIsBusy);
        OpenPhpMyAdminSettingsCommand = new RelayCommand(_ => OpenPhpMyAdminSettings(), _ => !PhpMyAdminIsBusy);
        OpenPhpMyAdminVersionsCommand = new RelayCommand(_ => OpenPhpMyAdminVersions(), _ => !PhpMyAdminIsBusy);
        InstallPhpMyAdminCommand = new RelayCommand(_ => OpenPhpMyAdminVersions(), _ => PhpMyAdminShowInstallButton && !PhpMyAdminIsBusy);

        OpenPhpRedisAdminCommand = new RelayCommand(_ => OpenPhpRedisAdmin(), _ => PhpRedisAdminCanOpen && !PhpRedisAdminIsBusy);
        OpenPhpRedisAdminSettingsCommand = new RelayCommand(_ => OpenPhpRedisAdminSettings(), _ => !PhpRedisAdminIsBusy);
        OpenPhpRedisAdminVersionsCommand = new RelayCommand(_ => OpenPhpRedisAdminVersions(), _ => !PhpRedisAdminIsBusy);
        InstallPhpRedisAdminCommand = new RelayCommand(_ => OpenPhpRedisAdminVersions(), _ => PhpRedisAdminShowInstallButton && !PhpRedisAdminIsBusy);

        _phpMyAdminAdminRow = new AdminUiToolViewModel
        {
            Name = "phpMyAdmin",
            Description = "Web UI for MySQL and MariaDB databases.",
            SettingsCommand = OpenPhpMyAdminSettingsCommand,
            InstallCommand = InstallPhpMyAdminCommand,
            VersionsCommand = OpenPhpMyAdminVersionsCommand,
            OpenCommand = OpenPhpMyAdminCommand
        };
        _phpRedisAdminAdminRow = new AdminUiToolViewModel
        {
            Name = "phpRedisAdmin",
            Description = "Web UI for Redis key inspection and management.",
            SettingsCommand = OpenPhpRedisAdminSettingsCommand,
            InstallCommand = InstallPhpRedisAdminCommand,
            VersionsCommand = OpenPhpRedisAdminVersionsCommand,
            OpenCommand = OpenPhpRedisAdminCommand
        };
        AdminTools = [_phpMyAdminAdminRow, _phpRedisAdminAdminRow];

        var webGroup = new ToolGroupViewModel { Title = "Web Panels" };
        webGroup.Items.Add(_phpMyAdminAdminRow);
        webGroup.Items.Add(_phpRedisAdminAdminRow);
        Groups.Add(webGroup);

        _ = RefreshAsync();
    }

    public ObservableCollection<ToolGroupViewModel> Groups { get; } = [];
    public ObservableCollection<CompatiblePhpOption> PhpMyAdminPhpVersions { get; }
    public ObservableCollection<AdminUiToolViewModel> AdminTools { get; }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand DismissMessageCommand { get; }
    public RelayCommand OpenPhpMyAdminCommand { get; }
    public RelayCommand OpenPhpMyAdminSettingsCommand { get; }
    public RelayCommand OpenPhpMyAdminVersionsCommand { get; }
    public RelayCommand InstallPhpMyAdminCommand { get; }
    public RelayCommand OpenPhpRedisAdminCommand { get; }
    public RelayCommand OpenPhpRedisAdminSettingsCommand { get; }
    public RelayCommand OpenPhpRedisAdminVersionsCommand { get; }
    public RelayCommand InstallPhpRedisAdminCommand { get; }

    public bool PhpMyAdminInstalled
    {
        get => _phpMyAdminInstalled;
        private set => SetProperty(ref _phpMyAdminInstalled, value);
    }

    public bool PhpMyAdminCanOpen
    {
        get => _phpMyAdminCanOpen;
        private set
        {
            if (SetProperty(ref _phpMyAdminCanOpen, value))
            {
                OpenPhpMyAdminCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool PhpMyAdminShowInstallButton
    {
        get => _phpMyAdminShowInstallButton;
        private set
        {
            if (SetProperty(ref _phpMyAdminShowInstallButton, value))
            {
                InstallPhpMyAdminCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool PhpMyAdminShowVersionsButton
    {
        get => _phpMyAdminShowVersionsButton;
        private set => SetProperty(ref _phpMyAdminShowVersionsButton, value);
    }

    public bool PhpMyAdminIsBusy
    {
        get => _phpMyAdminIsBusy;
        private set
        {
            if (SetProperty(ref _phpMyAdminIsBusy, value))
            {
                _phpMyAdminAdminRow.IsBusy = value;
                OpenPhpMyAdminCommand.RaiseCanExecuteChanged();
                OpenPhpMyAdminSettingsCommand.RaiseCanExecuteChanged();
                OpenPhpMyAdminVersionsCommand.RaiseCanExecuteChanged();
                InstallPhpMyAdminCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string PhpMyAdminStatus
    {
        get => _phpMyAdminStatus;
        private set => SetProperty(ref _phpMyAdminStatus, value);
    }

    public string PhpMyAdminUrl
    {
        get => _phpMyAdminUrl;
        private set => SetProperty(ref _phpMyAdminUrl, value);
    }

    public string PhpMyAdminActivePackageLabel
    {
        get => _phpMyAdminActivePackageLabel;
        private set => SetProperty(ref _phpMyAdminActivePackageLabel, value);
    }

    public string PhpMyAdminPhpVersionBadge
    {
        get => _phpMyAdminPhpVersionBadge;
        private set => SetProperty(ref _phpMyAdminPhpVersionBadge, value);
    }

    public string? SelectedPhpMyAdminPhpVersionId
    {
        get => _selectedPhpMyAdminPhpVersionId;
        set
        {
            if (!SetProperty(ref _selectedPhpMyAdminPhpVersionId, string.IsNullOrWhiteSpace(value) ? null : value))
            {
                return;
            }

            if (_suppressPhpMyAdminPhpVersionPersist || !PhpMyAdminInstalled || PhpMyAdminIsBusy)
            {
                return;
            }

            var normalized = string.IsNullOrWhiteSpace(value) ? null : value;
            var current = _phpMyAdminManager.GetStatus().PhpVersionId;
            if (string.Equals(current, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _phpMyAdminManager.ApplyConfig(new PhpMyAdminConfigUpdate
            {
                PhpVersionId = string.IsNullOrWhiteSpace(value) ? null : value
            });
            Message = "phpMyAdmin PHP version updated.";
            _ = ApplyPhpMyAdminConfigurationAsync();
        }
    }

    public bool PhpRedisAdminEnabled
    {
        get => _phpRedisAdminEnabled;
        set
        {
            if (!SetProperty(ref _phpRedisAdminEnabled, value))
            {
                return;
            }

            if (_suppressPhpRedisAdminEnabledPersist || !PhpRedisAdminInstalled || PhpRedisAdminIsBusy)
            {
                return;
            }

            _phpRedisAdminManager.ApplyConfig(new PhpRedisAdminConfigUpdate { Enabled = value });
            Message = value ? "phpRedisAdmin enabled." : "phpRedisAdmin disabled.";
            _ = ApplyAndRefreshPhpRedisAdminAsync();
        }
    }

    public bool PhpRedisAdminInstalled
    {
        get => _phpRedisAdminInstalled;
        private set => SetProperty(ref _phpRedisAdminInstalled, value);
    }

    public string PhpRedisAdminStatus
    {
        get => _phpRedisAdminStatus;
        private set => SetProperty(ref _phpRedisAdminStatus, value);
    }

    public string PhpRedisAdminMessage
    {
        get => _phpRedisAdminMessage;
        private set => SetProperty(ref _phpRedisAdminMessage, value);
    }

    public string PhpRedisAdminUrl
    {
        get => _phpRedisAdminUrl;
        private set => SetProperty(ref _phpRedisAdminUrl, value);
    }

    public string PhpRedisAdminOpenLabel
    {
        get => _phpRedisAdminOpenLabel;
        private set => SetProperty(ref _phpRedisAdminOpenLabel, value);
    }

    public string PhpRedisAdminPhpVersionBadge
    {
        get => _phpRedisAdminPhpVersionBadge;
        private set => SetProperty(ref _phpRedisAdminPhpVersionBadge, value);
    }

    public string PhpRedisAdminActivePackageLabel
    {
        get => _phpRedisAdminActivePackageLabel;
        private set => SetProperty(ref _phpRedisAdminActivePackageLabel, value);
    }

    public bool PhpRedisAdminCanOpen
    {
        get => _phpRedisAdminCanOpen;
        private set
        {
            if (SetProperty(ref _phpRedisAdminCanOpen, value))
            {
                OpenPhpRedisAdminCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool PhpRedisAdminShowInstallButton
    {
        get => _phpRedisAdminShowInstallButton;
        private set
        {
            if (SetProperty(ref _phpRedisAdminShowInstallButton, value))
            {
                InstallPhpRedisAdminCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool PhpRedisAdminShowVersionsButton
    {
        get => _phpRedisAdminShowVersionsButton;
        private set => SetProperty(ref _phpRedisAdminShowVersionsButton, value);
    }

    public bool PhpRedisAdminIsBusy
    {
        get => _phpRedisAdminIsBusy;
        private set
        {
            if (SetProperty(ref _phpRedisAdminIsBusy, value))
            {
                _phpRedisAdminAdminRow.IsBusy = value;
                OpenPhpRedisAdminCommand.RaiseCanExecuteChanged();
                OpenPhpRedisAdminSettingsCommand.RaiseCanExecuteChanged();
                OpenPhpRedisAdminVersionsCommand.RaiseCanExecuteChanged();
                InstallPhpRedisAdminCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasInstalledPhp
    {
        get => _hasInstalledPhp;
        private set => SetProperty(ref _hasInstalledPhp, value);
    }

    public bool ShowPhpMyAdminPhpSelector => PhpMyAdminInstalled && HasInstalledPhp;

    public string? Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
            {
                RaisePropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    public string? UpdatesMessage { get; private set; }
    public bool HasUpdates => !string.IsNullOrWhiteSpace(UpdatesMessage);

    public RelayCommand CheckUpdatesCommand { get; }
    public RelayCommand DismissUpdatesCommand { get; }

    private async Task CheckUpdatesAsync()
    {
        UpdatesMessage = "Checking…";
        RaisePropertyChanged(nameof(UpdatesMessage));
        RaisePropertyChanged(nameof(HasUpdates));

        try
        {
            await Task.Run(() =>
            {
                var packagesToCheck = new[] { PackageType.Composer, PackageType.WpCli, PackageType.Php };
                var updates = new List<string>();

                foreach (var type in packagesToCheck)
                {
                    var installed = _registryStore.List(type)
                        .OrderByDescending(p => p.Version, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();
                    if (installed is null) continue;

                    var latest = _catalogStore.List(type)
                        .OrderByDescending(e => e.Version, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();
                    if (latest is null) continue;

                    if (string.Compare(installed.Version, latest.Version, StringComparison.OrdinalIgnoreCase) < 0)
                        updates.Add($"{type}: {installed.Version} → {latest.Version}");
                }

                UpdatesMessage = updates.Count == 0
                    ? "✅ All tools are up to date."
                    : $"📦 {updates.Count} update(s): {string.Join(", ", updates)}";
            });
        }
        catch (Exception ex)
        {
            UpdatesMessage = $"Check failed: {ex.Message}";
        }
        finally
        {
            RaisePropertyChanged(nameof(UpdatesMessage));
            RaisePropertyChanged(nameof(HasUpdates));
        }
    }

    private ServicePackageToolCardViewModel CreateCliTool(PackageType type, string name, string? hint = null)
    {
        ServicePackageToolCardViewModel? card = null;
        card = new ServicePackageToolCardViewModel
        {
            Name = name,
            Hint = hint,
            PackageType = type,
            InstallCommand = new RelayCommand(_ => OpenToolPackageVersions(type, name), _ => card!.ShowInstallButton && !card.IsBusy && !card.IsDownloading && !card.IsBlocked),
            VersionsCommand = new RelayCommand(_ => OpenToolPackageVersions(type, name), _ => card!.ShowVersionsButton && !card.IsBusy && !card.IsDownloading && !card.IsBlocked)
        };

        return card;
    }

    private ServicePackageToolCardViewModel? FindCliTool(PackageType type) =>
        Groups.SelectMany(g => g.Items).OfType<ServicePackageToolCardViewModel>()
            .FirstOrDefault(tool => tool.PackageType == type);

    private Task RefreshAsync()
    {
        HasInstalledPhp = _registryStore.List(PackageType.Php).Count > 0;
        RefreshCliTools();

        var phpStatus = _phpMyAdminManager.GetStatus();
        PhpMyAdminInstalled = phpStatus.PackageInstalled;
        PhpMyAdminStatus = phpStatus.Ready
            ? "Ready"
            : !string.IsNullOrWhiteSpace(phpStatus.Message)
                ? phpStatus.Message
                : phpStatus.Enabled ? "Pending setup" : "Disabled";
        PhpMyAdminUrl = phpStatus.Url;
        PhpMyAdminCanOpen = phpStatus.Ready && !string.IsNullOrWhiteSpace(phpStatus.Url);
        PhpMyAdminShowInstallButton = !phpStatus.PackageInstalled;
        PhpMyAdminShowVersionsButton = phpStatus.PackageInstalled;
        PhpMyAdminActivePackageLabel = ResolvePackageLabel(phpStatus.PackageId);
        PhpMyAdminPhpVersionBadge = BuildPhpVersionBadge(phpStatus.PhpVersionId, phpStatus.PhpVersionCompatible, phpStatus.PackageInstalled);
        _suppressPhpMyAdminPhpVersionPersist = true;
        try
        {
            RefreshPhpMyAdminPhpVersions(phpStatus.PackageId);
            SelectedPhpMyAdminPhpVersionId = phpStatus.PhpVersionId;
        }
        finally
        {
            _suppressPhpMyAdminPhpVersionPersist = false;
        }

        var praStatus = _phpRedisAdminManager.GetStatus();
        PhpRedisAdminInstalled = praStatus.PackageInstalled;
        _suppressPhpRedisAdminEnabledPersist = true;
        PhpRedisAdminEnabled = praStatus.Enabled;
        _suppressPhpRedisAdminEnabledPersist = false;
        PhpRedisAdminStatus = praStatus.Ready ? "Ready" : praStatus.Enabled ? "Pending setup" : "Disabled";
        PhpRedisAdminMessage = praStatus.Message ?? string.Empty;
        PhpRedisAdminUrl = praStatus.Url;
        PhpRedisAdminOpenLabel = praStatus.OpenLabel;
        PhpRedisAdminCanOpen = praStatus.Ready && !string.IsNullOrWhiteSpace(praStatus.Url);
        PhpRedisAdminShowInstallButton = !praStatus.PackageInstalled;
        PhpRedisAdminShowVersionsButton = praStatus.PackageInstalled;
        PhpRedisAdminActivePackageLabel = ResolvePackageLabel(praStatus.PackageId);
        PhpRedisAdminPhpVersionBadge = BuildPhpVersionBadge(praStatus.PhpVersionId, praStatus.PhpVersionCompatible, praStatus.PackageInstalled);

        UpdateAdminToolRows(phpStatus, praStatus);
        RaisePropertyChanged(nameof(ShowPhpMyAdminPhpSelector));
        return Task.CompletedTask;
    }

    private void UpdateAdminToolRows(PhpMyAdminStatus phpStatus, PhpRedisAdminStatus praStatus)
    {
        _phpMyAdminAdminRow.Installed = phpStatus.PackageInstalled;
        _phpMyAdminAdminRow.IsReady = phpStatus.Ready;
        _phpMyAdminAdminRow.IsBusy = PhpMyAdminIsBusy;
        _phpMyAdminAdminRow.ShowInstallButton = PhpMyAdminShowInstallButton;
        _phpMyAdminAdminRow.ShowVersionsButton = PhpMyAdminShowVersionsButton;
        _phpMyAdminAdminRow.ShowOpenButton = PhpMyAdminCanOpen;
        _phpMyAdminAdminRow.BadgeText = PhpMyAdminPhpVersionBadge;
        _phpMyAdminAdminRow.StatusText = PhpMyAdminStatus;
        _phpMyAdminAdminRow.StatusColor = phpStatus.Ready ? "#8FD6B6" : "#91A0B5";
        _phpMyAdminAdminRow.Message = string.Empty;
        _phpMyAdminAdminRow.DetailsText = BuildAdminDetailsText(
            phpStatus.PackageInstalled,
            PhpMyAdminActivePackageLabel,
            PhpMyAdminUrl);

        _phpRedisAdminAdminRow.Installed = praStatus.PackageInstalled;
        _phpRedisAdminAdminRow.IsReady = praStatus.Ready;
        _phpRedisAdminAdminRow.IsBusy = PhpRedisAdminIsBusy;
        _phpRedisAdminAdminRow.ShowInstallButton = PhpRedisAdminShowInstallButton;
        _phpRedisAdminAdminRow.ShowVersionsButton = PhpRedisAdminShowVersionsButton;
        _phpRedisAdminAdminRow.ShowOpenButton = PhpRedisAdminCanOpen;
        _phpRedisAdminAdminRow.BadgeText = PhpRedisAdminPhpVersionBadge;
        _phpRedisAdminAdminRow.StatusText = PhpRedisAdminStatus;
        _phpRedisAdminAdminRow.StatusColor = praStatus.Ready ? "#8FD6B6" : "#91A0B5";
        _phpRedisAdminAdminRow.Message = PhpRedisAdminMessage;
        _phpRedisAdminAdminRow.DetailsText = BuildAdminDetailsText(
            praStatus.PackageInstalled,
            PhpRedisAdminActivePackageLabel,
            PhpRedisAdminUrl,
            PhpRedisAdminOpenLabel);
    }

    private static string BuildAdminDetailsText(
        bool installed,
        string versionLabel,
        string? url,
        string? extra = null)
    {
        if (!installed)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(versionLabel) && versionLabel != "—")
        {
            parts.Add($"Version: {versionLabel}");
        }

        if (!string.IsNullOrWhiteSpace(url))
        {
            parts.Add(url);
        }

        if (!string.IsNullOrWhiteSpace(extra))
        {
            parts.Add(extra);
        }

        return string.Join(" · ", parts);
    }

    private bool IsNodeRuntimeReady()
    {
        var settings = _settingsStore.Load();
        if (string.IsNullOrWhiteSpace(settings.Node.NvmPackageId))
        {
            return false;
        }

        if (_registryStore.GetById(settings.Node.NvmPackageId) is null)
        {
            return false;
        }

        return File.Exists(Path.Combine(NodePaths.SymlinkPath(_paths), "node.exe"));
    }

    private void RefreshCliTools()
    {
        var nodeReady = IsNodeRuntimeReady();

        foreach (var tool in Groups.SelectMany(g => g.Items).OfType<ServicePackageToolCardViewModel>())
        {
            var installed = _registryStore.List(tool.PackageType);
            tool.ShowInstallButton = installed.Count == 0;
            tool.ShowVersionsButton = installed.Count > 0;
            tool.VersionLabel = installed.Count == 0
                ? "No version"
                : installed
                    .OrderByDescending(pkg => pkg.Version, StringComparer.OrdinalIgnoreCase)
                    .Select(pkg => _catalogStore.GetById(pkg.Id)?.Label ?? pkg.Version)
                    .FirstOrDefault() ?? "Installed";

            tool.IsBlocked = false;
            tool.StatusNote = null;

            if (tool.PackageType is PackageType.Pnpm or PackageType.Vite && !nodeReady)
            {
                tool.StatusNote = "Requires Node — install nvm from the Node page first";
                tool.IsBlocked = true;
            }
            else if (tool.PackageType == PackageType.Composer && !HasInstalledPhp)
            {
                tool.StatusNote = "Requires PHP — install a PHP version first";
                tool.IsBlocked = true;
            }
            else if (tool.PackageType == PackageType.Laravel)
            {
                var hasComposer = _registryStore.List(PackageType.Composer).Count > 0;
                if (!HasInstalledPhp)
                {
                    tool.StatusNote = "Requires PHP — install a PHP version first";
                    tool.IsBlocked = true;
                }
                else if (!hasComposer)
                {
                    tool.StatusNote = "Requires Composer — install Composer first";
                    tool.IsBlocked = true;
                }
            }
        }

        RefreshCliToolInstallStates();
    }

    private void RefreshCliToolInstallStates()
    {
        foreach (var tool in Groups.SelectMany(g => g.Items).OfType<ServicePackageToolCardViewModel>())
        {
            var prefix = $"{tool.PackageType.ToString().ToLowerInvariant()}-";
            var progress = _installTracker.Items.FirstOrDefault(item =>
                item.IsActive && item.PackageId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            tool.IsDownloading = progress is not null;
            tool.InstallPercent = progress?.Percent ?? 0;
            tool.InstallMessage = progress?.Message ?? string.Empty;
            tool.InstallCommand.RaiseCanExecuteChanged();
            tool.VersionsCommand.RaiseCanExecuteChanged();
        }
    }

    private void RefreshPhpMyAdminPhpVersions(string packageId)
    {
        PhpMyAdminPhpVersions.Clear();
        PhpMyAdminPhpVersions.Add(new CompatiblePhpOption { Id = string.Empty, Label = "Auto — compatible PHP", Compatible = true });
        foreach (var version in _phpMyAdminManager.ListCompatiblePhpVersions(packageId))
        {
            PhpMyAdminPhpVersions.Add(new CompatiblePhpOption
            {
                Id = version.Id,
                Label = version.Compatible ? version.Label : $"{version.Label} — not supported",
                Compatible = version.Compatible
            });
        }
    }

    private static string BuildPhpVersionBadge(string? phpVersionId, bool? compatible, bool installed)
    {
        if (string.IsNullOrWhiteSpace(phpVersionId))
        {
            return installed ? "PHP: auto" : string.Empty;
        }

        var version = phpVersionId.StartsWith("php-", StringComparison.OrdinalIgnoreCase)
            ? phpVersionId["php-".Length..]
            : phpVersionId;
        if (compatible == false)
        {
            return $"PHP {version} (incompatible)";
        }

        return $"PHP {version}";
    }

    private void OpenToolPackageVersions(PackageType type, string label)
    {
        var catalog = _catalogStore.List(type);
        if (catalog.Count == 0)
        {
            StackrootDialogs.ShowWarning(
                Application.Current?.MainWindow,
                "Package catalog empty",
                $"No {label} versions were found.",
                $"Catalog file:{Environment.NewLine}{_catalogStore.Path}{Environment.NewLine}{Environment.NewLine}Restart the app after updating Stackroot.");
            return;
        }

        var dialogVm = new PackageVersionsDialogViewModel(
            $"{label} versions",
            VersionsHint,
            catalog,
            _registryStore,
            () => _registryStore.List(type)
                .OrderByDescending(pkg => pkg.Version, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()
                ?.Id,
            package => InstallToolPackageAsync(type, package),
            package => UninstallToolPackageAsync(type, package));

        ShowPackageVersionsDialog(dialogVm);
    }

    private async Task InstallToolPackageAsync(PackageType type, PackageEntry package)
    {
        if (_packages.IsInstalling(package.Id))
        {
            Message = $"{package.Label} is already downloading.";
            return;
        }

        var tool = FindCliTool(type);
        if (tool is not null)
        {
            tool.IsBusy = true;
        }

        try
        {
            if (_registryStore.IsInstalled(package.Id))
            {
                return;
            }

            await _packages.InstallAsync(package);
            Message = $"Installed {package.Label}.";
            _activity.LogSuccess("Tools", SessionActivityMessages.PackageInstalled(package.Label));
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Message = ex.Message;
            await RefreshAsync();
            throw;
        }
        finally
        {
            if (tool is not null)
            {
                tool.IsBusy = false;
            }
        }
    }

    private async Task UninstallToolPackageAsync(PackageType type, PackageEntry package)
    {
        if (!StackrootDialogs.ConfirmWarning(
                Application.Current?.MainWindow,
                $"Uninstall {package.Label}?",
                "Files will be removed from the runtime folder."))
        {
            return;
        }

        var tool = FindCliTool(type);
        if (tool is not null)
        {
            tool.IsBusy = true;
        }

        try
        {
            await _packages.UninstallAsync(package);
            Message = $"Removed {package.Label}.";
            _activity.LogSuccess("Tools", SessionActivityMessages.PackageUninstalled(package.Label));
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Message = ex.Message;
            await RefreshAsync();
        }
        finally
        {
            if (tool is not null)
            {
                tool.IsBusy = false;
            }
        }
    }

    private void OpenPhpMyAdmin()
    {
        var nginxRunning = _dashboardViewModel.Services
            .FirstOrDefault(s => s.ServiceKey == "nginx")?.IsRunning == true;
        if (!nginxRunning)
        {
            ConfirmDialog.Show(
                System.Windows.Application.Current?.MainWindow,
                "Nginx not running",
                "phpMyAdmin requires the web server. Start Nginx first from the Dashboard or Services page.");
            return;
        }

        if (string.IsNullOrWhiteSpace(PhpMyAdminUrl))
        {
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = PhpMyAdminUrl, UseShellExecute = true });
    }

    private void OpenPhpMyAdminSettings()
    {
        var dialogVm = new PhpMyAdminSettingsDialogViewModel(_phpMyAdminManager, _settingsStore);
        var owner = Application.Current?.MainWindow;
        var dialog = new PhpMyAdminSettingsDialog
        {
            DataContext = dialogVm,
            Owner = owner
        };

        dialogVm.RequestClose += (_, _) => dialog.Close();

        SettingsSaveFeedback.DeferredSettingsSave? deferred = null;
        dialogVm.SettingsSaved += (_, _) =>
        {
            deferred = new SettingsSaveFeedback.DeferredSettingsSave(
                "Applying phpMyAdmin settings…",
                "phpMyAdmin settings saved.",
                async () =>
                {
                    await ConfigurePhpMyAdminStackAsync().ConfigureAwait(true);
                    await RefreshAsync();
                });
        };

        dialog.ShowDialog();

        if (deferred is { } save)
        {
            _ = SettingsSaveFeedback.RunDeferredOnSessionActivityAsync(_activity, save);
        }
    }

    private void OpenPhpMyAdminVersions()
    {
        var catalog = _catalogStore.List(PackageType.Phpmyadmin);
        if (catalog.Count == 0)
        {
            StackrootDialogs.ShowWarning(
                Application.Current?.MainWindow,
                "Package catalog empty",
                "No phpMyAdmin versions were found.",
                $"Catalog file:{Environment.NewLine}{_catalogStore.Path}{Environment.NewLine}{Environment.NewLine}Restart the app after updating Stackroot.");
            return;
        }

        var dialogVm = new PackageVersionsDialogViewModel(
            "phpMyAdmin versions",
            VersionsHint,
            catalog,
            _registryStore,
            () =>
            {
                var current = _settingsStore.Load().Phpmyadmin.PackageId;
                return string.IsNullOrWhiteSpace(current)
                    ? SettingsDefaults.DefaultPhpMyAdminPackageId
                    : current;
            },
            package => InstallPhpMyAdminPackageAsync(package),
            package => UninstallPhpMyAdminPackageAsync(package),
            package => SetActivePhpMyAdminPackageAsync(package));

        ShowPackageVersionsDialog(dialogVm);
    }

    private async Task InstallPhpMyAdminPackageAsync(PackageEntry package)
    {
        PhpMyAdminIsBusy = true;
        try
        {
            if (_registryStore.IsInstalled(package.Id))
            {
                return;
            }

            if (!PackageInstallWithPhpDialogHost.TryPrompt(
                    Application.Current?.MainWindow,
                    package,
                    _catalogStore,
                    _registryStore,
                    AdminToolPhpResolver.DefaultPhpMyAdminRequirement,
                    out var phpVersionId))
            {
                return;
            }

            var phpPackage = _catalogStore.GetById(phpVersionId!)
                ?? throw new InvalidOperationException($"Catalog package '{phpVersionId}' was not found.");
            Message = _registryStore.IsInstalled(phpVersionId!)
                ? $"Installing {package.Label}…"
                : $"Installing {phpPackage.Label}, then {package.Label}…";

            await PackageInstallWithPhpDialogHost.EnsurePhpInstalledAsync(
                phpVersionId!,
                _catalogStore,
                _registryStore,
                _packages);

            var settings = _settingsStore.Load();
            _settingsStore.UpdatePhp(settings.Php with { ActiveVersionId = phpVersionId });
            _phpConfigWriter.WritePhpConfig(_settingsStore.Load(), phpVersionId);
            await _phpViewModel.RefreshNowAsync();

            Message = $"Installing {package.Label}…";
            await Task.Run(async () => await _packages.InstallAsync(package).ConfigureAwait(false))
                .ConfigureAwait(false);
            _phpMyAdminManager.ApplyConfig(new PhpMyAdminConfigUpdate
            {
                PackageId = package.Id,
                Enabled = true,
                PhpVersionId = phpVersionId
            });

            Message = $"Configuring {package.Label}…";
            await ConfigurePhpMyAdminStackAsync().ConfigureAwait(true);
            Message = $"Installed {package.Label}.";
            _activity.LogSuccess("Tools", SessionActivityMessages.PackageInstalled(package.Label));
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Message = ex.Message;
            await RefreshAsync();
        }
        finally
        {
            PhpMyAdminIsBusy = false;
        }
    }

    private Task ApplyPhpMyAdminConfigurationAsync() =>
        SettingsSaveFeedback.RunAsync(
            msg => Message = msg,
            "Applying phpMyAdmin settings…",
            "phpMyAdmin settings saved.",
            async () =>
            {
                await ConfigurePhpMyAdminStackAsync().ConfigureAwait(true);
                await RefreshAsync();
            });

    private async Task ConfigurePhpMyAdminStackAsync()
    {
        await Task.Run(async () =>
        {
            await _nginxWebStackRebuilder.ApplyAdminToolAndReloadAsync(
                () => _phpMyAdminManager.ApplyAsync(),
                forceNginxRestart: true).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await _dashboardViewModel.RefreshAfterStartupAsync().ConfigureAwait(false);
    }

    private async Task SetActivePhpMyAdminPackageAsync(PackageEntry package)
    {
        _phpMyAdminManager.ApplyConfig(new PhpMyAdminConfigUpdate { PackageId = package.Id });
        await _phpMyAdminManager.ApplyAsync();
        Message = $"{package.Label} is now the active version.";
        await RefreshAsync();
    }

    private async Task UninstallPhpMyAdminPackageAsync(PackageEntry package)
    {
        if (!StackrootDialogs.ConfirmWarning(
                Application.Current?.MainWindow,
                "Uninstall phpMyAdmin?",
                $"Uninstall {package.Label}? Files will be removed from the runtime folder."))
        {
            return;
        }

        PhpMyAdminIsBusy = true;
        try
        {
            await _packages.UninstallAsync(package);
            await _phpMyAdminManager.ApplyAsync();
            _appDomainConfigWriter.Write();
            Message = $"Removed {package.Label}.";
            _activity.LogSuccess("Tools", SessionActivityMessages.PackageUninstalled(package.Label));
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Message = ex.Message;
            await RefreshAsync();
        }
        finally
        {
            PhpMyAdminIsBusy = false;
        }
    }

    private void OpenPhpRedisAdmin()
    {
        var nginxRunning = _dashboardViewModel.Services
            .FirstOrDefault(s => s.ServiceKey == "nginx")?.IsRunning == true;
        if (!nginxRunning)
        {
            ConfirmDialog.Show(
                System.Windows.Application.Current?.MainWindow,
                "Nginx not running",
                "phpRedisAdmin requires the web server. Start Nginx first from the Dashboard or Services page.");
            return;
        }

        if (string.IsNullOrWhiteSpace(PhpRedisAdminUrl))
        {
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = PhpRedisAdminUrl, UseShellExecute = true });
    }

    private void OpenPhpRedisAdminSettings()
    {
        var dialogVm = new PhpRedisAdminSettingsDialogViewModel(_phpRedisAdminManager, _settingsStore);
        var owner = Application.Current?.MainWindow;
        var dialog = new PhpRedisAdminSettingsDialog
        {
            DataContext = dialogVm,
            Owner = owner
        };

        dialogVm.RequestClose += (_, _) => dialog.Close();

        SettingsSaveFeedback.DeferredSettingsSave? deferred = null;
        dialogVm.SettingsSaved += (_, _) =>
        {
            deferred = new SettingsSaveFeedback.DeferredSettingsSave(
                "Applying phpRedisAdmin settings…",
                "phpRedisAdmin settings saved.",
                ConfigurePhpRedisAdminStackAsync);
        };

        dialog.ShowDialog();

        if (deferred is { } save)
        {
            _ = SettingsSaveFeedback.RunDeferredOnSessionActivityAsync(_activity, save);
        }
    }

    private void OpenPhpRedisAdminVersions()
    {
        var catalog = _catalogStore.List(PackageType.Phpredisadmin);
        if (catalog.Count == 0)
        {
            StackrootDialogs.ShowWarning(
                Application.Current?.MainWindow,
                "Package catalog empty",
                "No phpRedisAdmin versions were found.",
                $"Catalog file:{Environment.NewLine}{_catalogStore.Path}{Environment.NewLine}{Environment.NewLine}Restart the app after updating Stackroot.");
            return;
        }

        var dialogVm = new PackageVersionsDialogViewModel(
            "phpRedisAdmin versions",
            VersionsHint,
            catalog,
            _registryStore,
            () =>
            {
                var current = _settingsStore.Load().Phpredisadmin.PackageId;
                return string.IsNullOrWhiteSpace(current)
                    ? SettingsDefaults.DefaultPhpRedisAdminPackageId
                    : current;
            },
            package => InstallPhpRedisAdminPackageAsync(package),
            package => UninstallPhpRedisAdminPackageAsync(package),
            package => SetActivePhpRedisAdminPackageAsync(package));

        ShowPackageVersionsDialog(dialogVm);
    }

    private async Task InstallPhpRedisAdminPackageAsync(PackageEntry package)
    {
        PhpRedisAdminIsBusy = true;
        try
        {
            if (_registryStore.IsInstalled(package.Id))
            {
                return;
            }

            if (!PackageInstallWithPhpDialogHost.TryPrompt(
                    Application.Current?.MainWindow,
                    package,
                    _catalogStore,
                    _registryStore,
                    AdminToolPhpResolver.DefaultPhpRedisAdminRequirement,
                    out var phpVersionId))
            {
                return;
            }

            var phpPackage = _catalogStore.GetById(phpVersionId!)
                ?? throw new InvalidOperationException($"Catalog package '{phpVersionId}' was not found.");
            Message = _registryStore.IsInstalled(phpVersionId!)
                ? $"Installing {package.Label}…"
                : $"Installing {phpPackage.Label}, then {package.Label}…";

            await PackageInstallWithPhpDialogHost.EnsurePhpInstalledAsync(
                phpVersionId!,
                _catalogStore,
                _registryStore,
                _packages);

            var settings = _settingsStore.Load();
            _settingsStore.UpdatePhp(settings.Php with { ActiveVersionId = phpVersionId });
            _phpConfigWriter.WritePhpConfig(_settingsStore.Load(), phpVersionId);
            await _phpViewModel.RefreshNowAsync();

            Message = $"Installing {package.Label}…";
            await _packages.InstallAsync(package);
            _phpRedisAdminManager.ApplyConfig(new PhpRedisAdminConfigUpdate
            {
                PackageId = package.Id,
                Enabled = true,
                PhpVersionId = phpVersionId
            });
            Message = $"Configuring {package.Label}…";
            await ConfigurePhpRedisAdminStackAsync().ConfigureAwait(true);
            Message = $"Installed {package.Label}.";
            _activity.LogSuccess("Tools", SessionActivityMessages.PackageInstalled(package.Label));
        }
        catch (Exception ex)
        {
            Message = ex.Message;
            await RefreshAsync();
        }
        finally
        {
            PhpRedisAdminIsBusy = false;
        }
    }

    private async Task SetActivePhpRedisAdminPackageAsync(PackageEntry package)
    {
        _phpRedisAdminManager.ApplyConfig(new PhpRedisAdminConfigUpdate { PackageId = package.Id });
        Message = $"{package.Label} is now the active version.";
        await ConfigurePhpRedisAdminStackAsync().ConfigureAwait(true);
    }

    private async Task UninstallPhpRedisAdminPackageAsync(PackageEntry package)
    {
        if (!StackrootDialogs.ConfirmWarning(
                Application.Current?.MainWindow,
                "Uninstall phpRedisAdmin?",
                $"Uninstall {package.Label}? Files will be removed from the runtime folder."))
        {
            return;
        }

        PhpRedisAdminIsBusy = true;
        try
        {
            await _packages.UninstallAsync(package);
            Message = $"Removed {package.Label}.";
            _activity.LogSuccess("Tools", SessionActivityMessages.PackageUninstalled(package.Label));
            await ApplyAndRefreshPhpRedisAdminAsync();
        }
        catch (Exception ex)
        {
            Message = ex.Message;
            await RefreshAsync();
        }
        finally
        {
            PhpRedisAdminIsBusy = false;
        }
    }

    private async Task ApplyAndRefreshPhpRedisAdminAsync()
    {
        try
        {
            await ConfigurePhpRedisAdminStackAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Message = ex.Message;
            await RefreshAsync();
        }
    }

    private Task ApplyPhpRedisAdminSettingsAsync() =>
        SettingsSaveFeedback.RunAsync(
            msg => Message = msg,
            "Applying phpRedisAdmin settings…",
            "phpRedisAdmin settings saved.",
            ConfigurePhpRedisAdminStackAsync);

    private async Task ConfigurePhpRedisAdminStackAsync()
    {
        await Task.Run(async () =>
        {
            await _nginxWebStackRebuilder.ApplyAdminToolAndReloadAsync(
                () => _phpRedisAdminManager.ApplyAsync(),
                forceNginxRestart: true).ConfigureAwait(false);
        }).ConfigureAwait(false);

        await _dashboardViewModel.RefreshAfterStartupAsync().ConfigureAwait(false);
        await RefreshAsync();
    }

    private static void ShowPackageVersionsDialog(PackageVersionsDialogViewModel dialogVm)
    {
        var owner = Application.Current?.MainWindow;
        var dialog = new PackageVersionsDialog
        {
            DataContext = dialogVm,
            Owner = owner
        };

        dialogVm.RequestClose += (_, _) => dialog.Close();
        dialog.ShowDialog();
    }

    private string ResolvePackageLabel(string? packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return "—";
        }

        return _catalogStore.GetById(packageId)?.Label ?? packageId;
    }
}
