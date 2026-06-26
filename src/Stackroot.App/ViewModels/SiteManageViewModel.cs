using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Stackroot.App.Commands;
using Stackroot.App.Localization;
using Stackroot.App.Helpers;
using Stackroot.App.Scheduling;
using Stackroot.App.Services;
using Stackroot.App.Views;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Abstractions.DataDocuments;
using Stackroot.Core.Catalog;
using Stackroot.Core.Databases;
using Stackroot.Core.Databases.Models;
using Stackroot.Core.IO;
using Stackroot.Core.Nginx;
using Stackroot.Core.Node;
using Stackroot.Core.Services;
using Stackroot.Core.Sites.Commands;
using Stackroot.Core.Sites.Installers;
using Stackroot.Core.Sites.Management;
using Stackroot.Core.Sites.Models;
using Stackroot.Core.Settings;
using Stackroot.Core.Supervisor;
using UpdateSiteInput = Stackroot.Core.Sites.Models.UpdateSiteInput;
using SiteModel = Stackroot.Core.Sites.Models.Site;

namespace Stackroot.App.ViewModels;

public sealed class SiteManageViewModel : ViewModelBase, IScheduledTaskRowHost
{
    private readonly SiteManager _siteManager;
    private readonly GlobalProcessManager _processManager;
    private readonly DatabaseManager _databaseManager;
    private readonly PackageCatalogStore _catalogStore;
    private readonly IServiceProvider _services;
    private readonly SettingsStore _settingsStore;
    private readonly SessionActivityReporter _activity;
    private readonly SessionActivityCoordinator _activityCoordinator;
    private readonly IDiagnosticsReporter _diagnostics;
    private readonly SiteThumbnailService _thumbnailService;
    private readonly StackrootPaths _paths;
    private readonly TaskSchedulerService _taskScheduler;
    private SiteModel? _site;
    private bool _isCapturing;
    private bool _isInstalling;
    private string _installStatus = string.Empty;
    private string? _postInstallPassword;
    private bool _passwordVisible;
    private bool _isRunningAction;
    private bool _isProcessBulkBusy;
    private string? _busyProcessId;
    private string _commandStatusMessage = string.Empty;
    private string _processStatusMessage = string.Empty;
    private string _databaseStatusMessage = string.Empty;
    private string _lastCommandLabel = string.Empty;
    private string _lastCommandLine = string.Empty;
    private int? _lastCommandExitCode;
    private long _lastCommandDurationMs;
    private string _lastCommandLogPath = string.Empty;
    private string? _lastQuickActionId;
    private bool _showCommandLogButton;
    private bool _isTogglingEnabled;

    public SiteManageViewModel(
        SiteManager siteManager,
        GlobalProcessManager processManager,
        DatabaseManager databaseManager,
        PackageCatalogStore catalogStore,
        IServiceProvider services,
        SessionActivityReporter activity,
        SessionActivityCoordinator activityCoordinator,
        IDiagnosticsReporter diagnostics,
        SettingsStore settingsStore,
        SiteThumbnailService thumbnailService,
        StackrootPaths paths,
        TaskSchedulerService taskScheduler)
    {
        _siteManager = siteManager;
        _processManager = processManager;
        _databaseManager = databaseManager;
        _catalogStore = catalogStore;
        _services = services;
        _activity = activity;
        _activityCoordinator = activityCoordinator;
        _diagnostics = diagnostics;
        _settingsStore = settingsStore;
        _thumbnailService = thumbnailService;
        _paths = paths;
        _taskScheduler = taskScheduler;

        _taskScheduler.TaskExecuted += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(SiteId))
            {
                return;
            }

            System.Windows.Application.Current?.Dispatcher.BeginInvoke(LoadScheduledTasks);
        };

        // Auto-refresh processes when any process changes (e.g. auto-start)
        _processManager.Changed += (_, _) =>
        {
            if (Site is not null)
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => RefreshProcesses());
        };

        SiteProcesses = [];
        QuickActionGroups = [];
        QuickActions = [];
        LinkedDatabases = [];
        ProcessPresets = [];
        ScheduledTasks = [];

        RunQuickActionCommand = new RelayCommand(actionId => _ = RunQuickActionAsync(actionId as string), _ => CanRunQuickAction());
        RefreshCommand = new RelayCommand(_ => RefreshSite(), _ => !string.IsNullOrWhiteSpace(SiteId));
        OpenTerminalCommand = new RelayCommand(_ => OpenTerminal(), _ => Site is not null && !string.IsNullOrWhiteSpace(Site.Path));
        OpenSiteCommand = new RelayCommand(_ => OpenSite(), _ => Site is not null && Site.Enabled);
        OpenFolderCommand = new RelayCommand(_ => OpenFolder(), _ => Site is not null && !string.IsNullOrWhiteSpace(Site.Path));
        AddProcessCommand = new RelayCommand(_ => OpenAddProcessDialog(), _ => Site is not null && !IsProcessBulkBusy);
        AddPresetCommand = new RelayCommand(presetId => AddProcessFromPreset(presetId as string), _ => Site is not null && !IsProcessBulkBusy);
        StartAllProcessesCommand = new RelayCommand(_ => _ = StartAllProcessesAsync(), _ => HasSiteProcesses && !IsProcessBulkBusy);
        StopAllProcessesCommand = new RelayCommand(_ => _ = StopAllProcessesAsync(), _ => HasSiteProcesses && !IsProcessBulkBusy);
        StartProcessCommand = new RelayCommand(id => _ = StartProcessAsync(id as string), id => CanRunProcessAction(id as string));
        StopProcessCommand = new RelayCommand(id => _ = StopProcessAsync(id as string), id => CanRunProcessAction(id as string));
        RestartProcessCommand = new RelayCommand(id => _ = RestartProcessAsync(id as string), id => CanRunProcessAction(id as string));
        ViewProcessLogCommand = new RelayCommand(row => OpenProcessLog(row as SiteProcessRowViewModel));
        EditProcessCommand = new RelayCommand(row => OpenEditProcess(row as SiteProcessRowViewModel));
        ToggleProcessEnabledCommand = new RelayCommand(row => ToggleProcessEnabled(row as SiteProcessRowViewModel));
        RemoveProcessCommand = new RelayCommand(row => RemoveProcess(row as SiteProcessRowViewModel));
        ViewCommandLogCommand = new RelayCommand(_ => OpenCommandLog(), _ => ShowCommandLogButton);
        CancelCommandStatusCommand = new RelayCommand(_ => _ = CancelRunningCommandAsync(), _ => CanCancelRunningCommand());
        DismissCommandStatusCommand = new RelayCommand(_ => ClearCommandStatus(), _ => ShowDismissCommandStatus);
        DismissProcessStatusCommand = new RelayCommand(_ => ClearProcessStatus());
        DismissDatabaseStatusCommand = new RelayCommand(_ => ClearDatabaseStatus());
        CreateDatabaseCommand = new RelayCommand(_ => OpenCreateDatabaseDialog(), _ => Site is not null);
        InstallSiteCommand = new RelayCommand(_ => _ = InstallSiteAsync(), _ => ShowInstallerButton && !_isInstalling);
        OpenPostInstallAdminCommand = new RelayCommand(_ => OpenPostInstallAdmin());
        CaptureThumbnailCommand = new RelayCommand(
            _ => _ = CaptureThumbnailAsync(forceRefresh: true),
            _ => Site is not null && Site.Enabled && !IsCapturing);
        CopyPasswordCommand = new RelayCommand(_ => Clipboard.SetText(PostInstallAdminPassword));
        TogglePasswordCommand = new RelayCommand(_ => PasswordVisible = !PasswordVisible);
        ChangePasswordCommand = new RelayCommand(_ => _ = ChangePasswordAsync());
        ManageCustomCommandsCommand = new RelayCommand(_ => OpenCustomCommandsDialog(), _ => Site is not null);
        DismissCustomStatusCommand = new RelayCommand(item =>
        {
            if (item is SiteCustomCommandViewModel vm)
            {
                vm.ClearStatus();
                ActiveCommandStatuses.Remove(vm);
                RaisePropertyChanged(nameof(ShowCustomCommandStatus));
            }
        }, item => item is SiteCustomCommandViewModel { IsRunning: false });
        OpenSslPathsCommand = new RelayCommand(_ => OpenSslPathsDialog(), _ => HasDevSslPaths);
        EditSiteCommand = new RelayCommand(_ => OpenEditSiteDialog(), _ => Site is not null);
        ToggleFeaturedCommand = new RelayCommand(_ => ToggleFeatured(), _ => Site is not null);
        ToggleEnabledCommand = new RelayCommand(_ => _ = ToggleEnabledAsync(), _ => Site is not null && !_isTogglingEnabled);
        AddScheduledTaskCommand = new RelayCommand(_ => OpenScheduledTaskDialog(), _ => Site is not null);
        EditScheduledTaskCommand = new RelayCommand(row => OpenScheduledTaskDialog(row as ScheduledTaskRowViewModel));
        RefreshScheduledTasksCommand = new RelayCommand(_ => LoadScheduledTasks());
        OpenAllScheduledTasksCommand = new RelayCommand(_ => _services.GetRequiredService<ShellViewModel>().Navigate("scheduled"));
        BackCommand = new RelayCommand(_ => _services.GetRequiredService<ShellViewModel>().Navigate("sites"));
    }

    public bool HasDevSslPaths => DevSslCertificateManager.TryGetExisting(_paths) is not null;

    public string SiteId { get; private set; } = string.Empty;

    public SiteModel? Site
    {
        get => _site;
        private set
        {
            if (SetProperty(ref _site, value))
            {
                RaisePropertyChanged(nameof(TemplateLabel));
                RaisePropertyChanged(nameof(PhpLabel));
                RaisePropertyChanged(nameof(SiteEnabled));
                RaisePropertyChanged(nameof(ShowHttpsBadge));
                RaisePropertyChanged(nameof(HasQuickActions));
                RaisePropertyChanged(nameof(ShowInstallerButton));
                RaisePropertyChanged(nameof(ShowPostInstallCard));
                RaisePropertyChanged(nameof(ShowWordPressPostInstallCard));
                RaisePropertyChanged(nameof(ShowLaravelPostInstallCard));
                RaisePropertyChanged(nameof(ShowStaticSiteCard));
                RaisePropertyChanged(nameof(InstallerButtonLabel));
                RaisePropertyChanged(nameof(PostInstallWpVersion));
                RaisePropertyChanged(nameof(PostInstallWpLabel));
                RaisePropertyChanged(nameof(PostInstallLaravelVersion));
                RaisePropertyChanged(nameof(PostInstallAdminPassword));
                RaisePropertyChanged(nameof(PostInstallPasswordDisplay));
                RaisePropertyChanged(nameof(HasThumbnail));
                RaisePropertyChanged(nameof(ThumbnailImage));
                RaisePropertyChanged(nameof(HasNoThumbnail));
                RaisePropertyChanged(nameof(ThumbnailsFeatureEnabled));
                RaisePropertyChanged(nameof(ShowCaptureButton));
                RaisePropertyChanged(nameof(ShowRefreshButton));
                RaiseSiteActionChromeChanged();
                OpenSiteCommand.RaiseCanExecuteChanged();
                OpenFolderCommand.RaiseCanExecuteChanged();
                CreateDatabaseCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string TemplateLabel => Site is null
        ? string.Empty
        : SiteTemplates.Resolve(Site.Template).Label;

    public string PhpLabel
    {
        get
        {
            if (Site is null || string.IsNullOrWhiteSpace(Site.PhpVersionId))
            {
                return "No PHP";
            }

            var entry = _catalogStore.GetById(Site.PhpVersionId);
            return entry?.Label ?? Site.PhpVersionId;
        }
    }

    public ObservableCollection<PhpVersionOptionViewModel> PhpVersions { get; } = [];
    public ObservableCollection<NodeVersionOptionViewModel> NodeVersions { get; } = [];

    public string SelectedPhpVersionId
    {
        get => Site?.PhpVersionId ?? "no-php";
        set
        {
            if (Site is null || string.Equals(value, SelectedPhpVersionId, StringComparison.Ordinal)) return;
            _siteManager.Update(Site.Id, new UpdateSiteInput { PhpVersionId = value == "no-php" ? null : value });
            RefreshSite();
        }
    }

    public string SelectedNodeVersionId
    {
        get => Site?.NodeVersionId ?? "none";
        set
        {
            if (Site is null || string.Equals(value, SelectedNodeVersionId, StringComparison.Ordinal)) return;
            _siteManager.Update(Site.Id, new UpdateSiteInput { NodeVersionId = value == "none" ? null : value });
            RefreshSite();
        }
    }

    public bool SiteEnabled => Site?.Enabled == true;
    public bool SiteDisabled => Site is not null && !Site.Enabled;
    public bool IsFeatured => Site?.Featured == true;
    public string FeaturedPinToolTip => IsFeatured
        ? LocalizationManager.Get("Loc.Common.RemoveFromFeatured", "Remove from featured")
        : LocalizationManager.Get("Loc.Common.PinToFeatured", "Pin to featured");
    public string EnableDisableLabel => SiteEnabled
        ? LocalizationManager.Get("Loc.Common.Disable", "Disable")
        : LocalizationManager.Get("Loc.Common.Enable", "Enable");
    public bool IsTogglingEnabled
    {
        get => _isTogglingEnabled;
        private set
        {
            if (SetProperty(ref _isTogglingEnabled, value))
            {
                RaisePropertyChanged(nameof(IsNotTogglingEnabled));
                RaisePropertyChanged(nameof(ToggleEnabledButtonLabel));
                ToggleEnabledCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsNotTogglingEnabled => !IsTogglingEnabled;
    public string ToggleEnabledButtonLabel => IsTogglingEnabled
        ? (SiteEnabled ? LocalizationManager.Get("Loc.Common.Disabling", "Disabling…") : LocalizationManager.Get("Loc.Common.Enabling", "Enabling…"))
        : EnableDisableLabel;

    public bool ShowHttpsBadge => Site?.ForceHttps == true;

    public bool ShowInstallerButton => Site is not null
        && SiteTemplates.Resolve(Site.Template).HasInstaller
        && !IsSiteAlreadyInstalled;

    private bool IsSiteAlreadyInstalled => Site switch
    {
        not null when Site.Template == SiteTemplateIds.Wordpress =>
            File.Exists(Path.Combine(Site.Path, "wp-config.php")),
        not null when Site.Template == SiteTemplateIds.Laravel =>
            File.Exists(Path.Combine(Site.Path, "artisan")),
        _ => false
    };

    public bool ShowPostInstallCard => Site is not null
        && SiteTemplates.Resolve(Site.Template).HasInstaller
        && IsSiteAlreadyInstalled;

    public bool ShowWordPressPostInstallCard => ShowPostInstallCard
        && Site?.Template == SiteTemplateIds.Wordpress;

    public bool ShowLaravelPostInstallCard => ShowPostInstallCard
        && Site?.Template == SiteTemplateIds.Laravel;

    public bool ShowStaticSiteCard => Site is not null
        && Site.Template == SiteTemplateIds.Static;

    private bool IsWordPressInstalled =>
        Site is not null
        && File.Exists(Path.Combine(Site.Path, "wp-config.php"));

    public string InstallerButtonLabel => Site is null ? string.Empty
        : $"Install {SiteTemplates.Resolve(Site.Template).Label}";

    public bool IsInstalling
    {
        get => _isInstalling;
        private set
        {
            if (SetProperty(ref _isInstalling, value))
                InstallSiteCommand.RaiseCanExecuteChanged();
        }
    }

    public string InstallStatus
    {
        get => _installStatus;
        private set => SetProperty(ref _installStatus, value);
    }

    public string PostInstallSiteUrl => Site is not null ? "http://" + Site.Domain : "";

    public string PostInstallAdminUser => "admin";

    public string PostInstallAdminPassword
    {
        get
        {
            if (_postInstallPassword is { Length: > 0 }) return _postInstallPassword;
            LoadCredentials();
            return _postInstallPassword ?? "";
        }
    }

    public string PostInstallPasswordDisplay => _passwordVisible
        ? PostInstallAdminPassword
        : new string('*', Math.Min(PostInstallAdminPassword.Length, 16));

    public bool PasswordVisible
    {
        get => _passwordVisible;
        set
        {
            if (SetProperty(ref _passwordVisible, value))
                RaisePropertyChanged(nameof(PostInstallPasswordDisplay));
        }
    }

    public string PostInstallWpVersion
    {
        get
        {
            if (Site is null) return "";
            // Check both Path and Path+DocumentRoot
            var root = Site.DocumentRoot is not null && Site.DocumentRoot != "."
                ? Path.Combine(Site.Path, Site.DocumentRoot)
                : Site.Path;
            var versionFile = Path.Combine(root, "wp-includes", "version.php");
            if (!File.Exists(versionFile))
                versionFile = Path.Combine(Site.Path, "wp-includes", "version.php");
            if (!File.Exists(versionFile)) return "";
            try
            {
                var content = File.ReadAllText(versionFile);
                var match = System.Text.RegularExpressions.Regex.Match(content, @"\$wp_version\s*=\s*'([^']+)'");
                var version = match.Success ? match.Groups[1].Value : "";
                if (string.IsNullOrEmpty(version))
                {
                    var m2 = System.Text.RegularExpressions.Regex.Match(content, @"\$wp_version\s*=\s*""([^""]+)""");
                    if (m2.Success) version = m2.Groups[1].Value;
                }
                return version;
            }
            catch { return ""; }
        }
    }

    public bool ShowPostInstallUpdate => false; // Future: check wp-cli for updates

    public string PostInstallWpLabel
    {
        get
        {
            var version = PostInstallWpVersion;
            return string.IsNullOrEmpty(version) ? "📘 WordPress" : $"📘 WordPress {version}";
        }
    }

    public string PostInstallLaravelVersion
    {
        get
        {
            if (Site is null) return "";
            var composerJson = Path.Combine(Site.Path, "composer.json");
            if (!File.Exists(composerJson)) return "";
            try
            {
                var json = File.ReadAllText(composerJson);
                var match = System.Text.RegularExpressions.Regex.Match(json, "\"laravel/framework\"\\s*:\\s*\"[^\"]*([0-9]+\\.[0-9]+\\.[0-9]+)");
                if (match.Success) return match.Groups[1].Value;

                // Fallback: read from vendor
                var versionFile = Path.Combine(Site.Path, "vendor", "laravel", "framework", "src", "Illuminate", "Foundation", "Application.php");
                if (File.Exists(versionFile))
                {
                    var content = File.ReadAllText(versionFile);
                    var vMatch = System.Text.RegularExpressions.Regex.Match(content, @"const VERSION = '([^']+)'");
                    if (vMatch.Success) return vMatch.Groups[1].Value;
                }
                return "";
            }
            catch { return ""; }
        }
    }

    public string PostInstallUpdateHint => "";
    public bool HasThumbnail => File.Exists(ThumbnailPath);
    public bool HasNoThumbnail => !HasThumbnail;

    public bool ThumbnailsFeatureEnabled =>
        (_settingsStore.Load().General.ThumbnailsEnabled ?? false) && HasThumbnail;

    public bool ThumbnailsFeatureDisabled =>
        !(_settingsStore.Load().General.ThumbnailsEnabled ?? false);

    public bool ShowThumbnailColumn =>
        (_settingsStore.Load().General.ThumbnailsEnabled ?? false);

    public bool IsCapturing
    {
        get => _isCapturing;
        private set
        {
            if (SetProperty(ref _isCapturing, value))
            {
                RaisePropertyChanged(nameof(IsNotCapturing));
                RaisePropertyChanged(nameof(ShowCaptureButton));
                RaisePropertyChanged(nameof(ShowRefreshButton));
                CaptureThumbnailCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsNotCapturing => !IsCapturing;

    public bool ShowCaptureButton => HasNoThumbnail && IsNotCapturing;
    public bool ShowRefreshButton => HasThumbnail && IsNotCapturing;
    public string ThumbnailPath => Site is not null
        ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Stackroot", "sites", Site.Id, "thumbnail.png")
        : "";

    public System.Windows.Media.Imaging.BitmapImage? ThumbnailImage
    {
        get
        {
            var path = ThumbnailPath;
            if (!File.Exists(path)) return null;
            try
            {
                // Use StreamSource to bypass WPF URI cache; FileShare.ReadWrite so Playwright can overwrite
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.StreamSource = stream;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }
    }

    public bool HasQuickActions => QuickActionGroups.Count > 0;
    public bool HasLinkedDatabases => LinkedDatabases.Count > 0;
    public bool ShowNoLinkedDatabases => !HasLinkedDatabases;

    // Custom commands per site
    private List<SiteCustomCommand> _customCommands = [];
    public ObservableCollection<SiteCustomCommandViewModel> CustomCommandItems { get; } = [];
    public ObservableCollection<SiteCustomCommandViewModel> ActiveCommandStatuses { get; } = [];
    public bool HasCustomCommands => CustomCommandItems.Count > 0;
    public bool ShowCustomCommandStatus => ActiveCommandStatuses.Count > 0;

    private void OpenCustomCommandsDialog()
    {
        if (Site is null)
        {
            return;
        }

        var siteDataDir = SiteDataDir;
        var dlgVm = new CustomCommandsDialogViewModel(
            siteDataDir,
            _customCommands,
            IsCustomCommandRunning);
        var dialog = new CustomCommandsDialog
        {
            DataContext = dlgVm,
            Owner = Application.Current?.MainWindow
        };
        var result = false;
        dlgVm.RequestClose += (_, saved) => { result = saved; dialog.Close(); };
        dialog.ShowDialog();

        if (!result)
        {
            return;
        }

        _customCommands = dlgVm.ToModels().ToList();
        SaveCustomCommands();
        LoadCustomCommandItems();
    }

    private bool IsCustomCommandRunning(string commandId) =>
        CustomCommandItems.FirstOrDefault(item => item.Id == commandId)?.IsRunning == true;

    private void LoadCustomCommandItems()
    {
        CustomCommandItems.Clear();
        foreach (var cmd in _customCommands)
        {
            var vm = new SiteCustomCommandViewModel
            {
                Id = cmd.Id,
                Label = cmd.Label,
                Command = cmd.Command,
                SitePath = Site?.Path ?? "",
                ForegroundHex = cmd.ForegroundHex,
                BackgroundHex = cmd.BackgroundHex,
                IconFilePath = CustomCommandIconStore.ResolvePath(SiteDataDir, cmd.IconFileName)
            };
            vm.RunCommand = new RelayCommand(_ => _ = RunCustomCommandTrackedAsync(vm, cmd.Command), _ => !vm.IsRunning);
            vm.CancelCommand = new RelayCommand(_ => _ = CancelCustomCommandAsync(vm), _ => vm.CanCancelCommand);
            vm.ViewLogCommand = new RelayCommand(_ => OpenCustomCommandLog(vm, openInExternalEditor: false), _ => vm.ShowViewLogButton);
            vm.OpenLogAction = openExternal => OpenCustomCommandLog(vm, openExternal);
            vm.IsCommandRunning = () =>
                !string.IsNullOrWhiteSpace(vm._logPath) &&
                _siteManager.IsSiteCommandRunning(vm._logPath!);
            vm.ChromeChanged = () => DismissCustomStatusCommand.RaiseCanExecuteChanged();
            CustomCommandItems.Add(vm);
        }

        RaisePropertyChanged(nameof(HasCustomCommands));
    }

    private void LoadCustomCommands()
    {
        _customCommands.Clear();
        if (Site is null) return;
        var path = CustomCommandsPath;
        if (!File.Exists(path)) return;
        try
        {
            var json = File.ReadAllText(path);
            var document = System.Text.Json.JsonSerializer.Deserialize<CustomCommandsDocument>(json, JsonSerializerConfig.Default);
            _customCommands = document?.Commands?.Select(entry => new SiteCustomCommand
            {
                Id = entry.Id,
                Label = entry.Label,
                Command = entry.Command,
                Runtime = entry.Runtime,
                ForegroundHex = entry.ForegroundHex,
                BackgroundHex = entry.BackgroundHex,
                IconFileName = entry.IconFileName
            }).ToList() ?? [];
        }
        catch { _customCommands = []; }
    }

    private void SaveCustomCommands()
    {
        if (Site is null) return;
        var path = CustomCommandsPath;
        var dir = Path.GetDirectoryName(path);
        if (dir is not null) Directory.CreateDirectory(dir);
        var document = new CustomCommandsDocument
        {
            SchemaVersion = DataDocumentSchemas.SiteCustomCommands,
            Commands = _customCommands.Select(command => new CustomCommandEntry
            {
                Id = command.Id,
                Label = command.Label,
                Command = command.Command,
                Runtime = command.Runtime,
                ForegroundHex = command.ForegroundHex,
                BackgroundHex = command.BackgroundHex,
                IconFileName = command.IconFileName
            }).ToList()
        };
        var json = System.Text.Json.JsonSerializer.Serialize(document, JsonSerializerConfig.Default);
        File.WriteAllText(path, json);
    }

    private string SiteDataDir => Site is not null
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Stackroot", "sites", Site.Id)
        : string.Empty;

    private string CustomCommandsPath => StackrootPathResolver.SiteCustomCommandsPath(SiteDataDir);

    public bool IsRunningAction
    {
        get => _isRunningAction;
        private set
        {
            if (SetProperty(ref _isRunningAction, value))
            {
                RaiseQuickActionCanExecute();
                RaiseProcessActionCanExecute();
                RaiseCommandStatusChromeChanged();
            }
        }
    }

    public bool IsProcessBulkBusy
    {
        get => _isProcessBulkBusy;
        private set
        {
            if (SetProperty(ref _isProcessBulkBusy, value))
            {
                AddProcessCommand.RaiseCanExecuteChanged();
                AddPresetCommand.RaiseCanExecuteChanged();
                StartAllProcessesCommand.RaiseCanExecuteChanged();
                StopAllProcessesCommand.RaiseCanExecuteChanged();
                RaiseProcessActionCanExecute();
            }
        }
    }

    public string CommandStatusMessage
    {
        get => _commandStatusMessage;
        private set
        {
            if (SetProperty(ref _commandStatusMessage, value))
            {
                RaisePropertyChanged(nameof(ShowCommandStatus));
            }
        }
    }

    public string ProcessStatusMessage
    {
        get => _processStatusMessage;
        private set
        {
            if (SetProperty(ref _processStatusMessage, value))
            {
                RaisePropertyChanged(nameof(ShowProcessStatus));
            }
        }
    }

    public string DatabaseStatusMessage
    {
        get => _databaseStatusMessage;
        private set
        {
            if (SetProperty(ref _databaseStatusMessage, value))
            {
                RaisePropertyChanged(nameof(ShowDatabaseStatus));
            }
        }
    }

    public bool ShowCommandStatus => !string.IsNullOrWhiteSpace(CommandStatusMessage);

    public bool ShowDismissCommandStatus => ShowCommandStatus && !IsRunningAction;

    public bool ShowCancelCommandStatus => IsRunningAction && CanCancelRunningCommand();
    public bool ShowProcessStatus => !string.IsNullOrWhiteSpace(ProcessStatusMessage);
    public bool ShowDatabaseStatus => !string.IsNullOrWhiteSpace(DatabaseStatusMessage);

    public bool ShowCommandLogButton
    {
        get => _showCommandLogButton;
        private set
        {
            if (SetProperty(ref _showCommandLogButton, value))
            {
                ViewCommandLogCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public ObservableCollection<SiteProcessRowViewModel> SiteProcesses { get; }
    public ObservableCollection<QuickActionGroupViewModel> QuickActionGroups { get; }
    public ObservableCollection<QuickActionItemViewModel> QuickActions { get; }
    public ObservableCollection<SiteLinkedDatabaseViewModel> LinkedDatabases { get; }
    public ObservableCollection<SiteProcessPresetViewModel> ProcessPresets { get; }
    public ObservableCollection<ScheduledTaskRowViewModel> ScheduledTasks { get; }
    public bool HasScheduledTasks => ScheduledTasks.Count > 0;
    public bool ShowNoScheduledTasks => !HasScheduledTasks;
    public bool HasSiteProcesses => SiteProcesses.Count > 0;
    public bool ShowNoSiteProcesses => !HasSiteProcesses;
    public bool HasProcessPresets => ProcessPresets.Count > 0;

    public RelayCommand RunQuickActionCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand OpenTerminalCommand { get; }
    public RelayCommand OpenSiteCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand AddProcessCommand { get; }
    public RelayCommand AddPresetCommand { get; }
    public RelayCommand StartAllProcessesCommand { get; }
    public RelayCommand StopAllProcessesCommand { get; }
    public RelayCommand StartProcessCommand { get; }
    public RelayCommand StopProcessCommand { get; }
    public RelayCommand RestartProcessCommand { get; }
    public RelayCommand ViewProcessLogCommand { get; }
    public RelayCommand EditProcessCommand { get; }
    public RelayCommand ToggleProcessEnabledCommand { get; }
    public RelayCommand RemoveProcessCommand { get; }
    public RelayCommand ViewCommandLogCommand { get; }
    public RelayCommand CancelCommandStatusCommand { get; }
    public RelayCommand DismissCommandStatusCommand { get; }
    public RelayCommand DismissProcessStatusCommand { get; }
    public RelayCommand DismissDatabaseStatusCommand { get; }
    public RelayCommand CreateDatabaseCommand { get; }
    public RelayCommand InstallSiteCommand { get; }
    public RelayCommand OpenPostInstallAdminCommand { get; }
    public RelayCommand CaptureThumbnailCommand { get; }
    public RelayCommand CopyPasswordCommand { get; }
    public RelayCommand TogglePasswordCommand { get; }
    public RelayCommand ChangePasswordCommand { get; }
    public RelayCommand ManageCustomCommandsCommand { get; }
    public RelayCommand DismissCustomStatusCommand { get; }
    public RelayCommand OpenSslPathsCommand { get; }
    public RelayCommand EditSiteCommand { get; }
    public RelayCommand ToggleFeaturedCommand { get; }
    public RelayCommand ToggleEnabledCommand { get; }
    public RelayCommand AddScheduledTaskCommand { get; }
    public RelayCommand EditScheduledTaskCommand { get; }
    public RelayCommand RefreshScheduledTasksCommand { get; }
    public RelayCommand OpenAllScheduledTasksCommand { get; }
    public RelayCommand BackCommand { get; }

    public void Load(string siteId)
    {
        SiteId = siteId.Trim();
        RefreshSite();
        RefreshCommand.RaiseCanExecuteChanged();
        AddProcessCommand.RaiseCanExecuteChanged();
    }

    private async void RefreshSite()
    {
        Site = _siteManager.Get(SiteId);
        ClearCommandStatus();
        ClearProcessStatus();
        ClearDatabaseStatus();
        if (Site is null)
        {
            CommandStatusMessage = $"Site not found: {SiteId}";
        }

        RebuildQuickActions();
        RebuildProcessPresets();
        RefreshLinkedDatabases();
        RaisePropertyChanged(nameof(HasDevSslPaths));
        OpenSslPathsCommand.RaiseCanExecuteChanged();
        RaiseQuickActionCanExecute();
        CleanupOldLoginFiles();
        LoadVersions();
        LoadCustomCommands();
        LoadCustomCommandItems();
        LoadScheduledTasks();
        RaiseSiteActionChromeChanged();

        await RefreshProcessesAsync();
    }

    private void LoadScheduledTasks()
    {
        ScheduledTasks.Clear();
        if (string.IsNullOrWhiteSpace(SiteId))
        {
            RaisePropertyChanged(nameof(HasScheduledTasks));
            RaisePropertyChanged(nameof(ShowNoScheduledTasks));
            return;
        }

        foreach (var task in _taskScheduler.List()
                     .Where(t => string.Equals(t.SiteId, SiteId, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(t => t.Label, StringComparer.OrdinalIgnoreCase))
        {
            ScheduledTasks.Add(new ScheduledTaskRowViewModel(task, this, _siteManager, showSiteLabel: false));
        }

        RaisePropertyChanged(nameof(HasScheduledTasks));
        RaisePropertyChanged(nameof(ShowNoScheduledTasks));
    }

    private void OpenScheduledTaskDialog(ScheduledTaskRowViewModel? existing = null)
    {
        if (Site is null)
        {
            return;
        }

        var dlgVm = new CronTaskDialogViewModel(_siteManager, existing?.Model, defaultSiteId: Site.Id);
        var dialog = new CronTaskDialog
        {
            DataContext = dlgVm,
            Owner = Application.Current?.MainWindow
        };
        var result = false;
        dlgVm.RequestClose += (_, r) => { result = r; dialog.Close(); };
        dialog.ShowDialog();

        if (!result)
        {
            return;
        }

        var model = dlgVm.ToModel(existing?.Model.Id);
        model.SiteId = Site.Id;
        if (existing is not null)
        {
            existing.Model.Label = model.Label;
            existing.Model.Command = model.Command;
            existing.Model.CronExpression = model.CronExpression;
            existing.Model.WorkingDirectory = model.WorkingDirectory;
            existing.Model.CaptureLog = model.CaptureLog;
            existing.Model.SiteId = model.SiteId;
            _taskScheduler.Update(existing.Model);
            existing.Refresh();
        }
        else
        {
            _taskScheduler.Add(model);
        }

        LoadScheduledTasks();
    }

    void IScheduledTaskRowHost.UpdateTask(ScheduledTaskModel model) => _taskScheduler.Update(model);

    Task IScheduledTaskRowHost.RunNowAndWaitAsync(string id) => _taskScheduler.RunNowAsync(id);

    void IScheduledTaskRowHost.DeleteTask(string id)
    {
        _taskScheduler.Delete(id);
        LoadScheduledTasks();
    }

    void IScheduledTaskRowHost.ReloadTasks() => LoadScheduledTasks();

    void IScheduledTaskRowHost.OpenTaskLog(string taskId, string logPath, string label, bool openInExternalEditor) =>
        OpenScheduledTaskLog(taskId, logPath, label, openInExternalEditor);

    private void OpenScheduledTaskLog(string taskId, string logPath, string label, bool openInExternalEditor)
    {
        var task = _taskScheduler.List().FirstOrDefault(t => t.Id == taskId);
        var running = false;
        var session = new SiteLogSession(logPath)
        {
            CommandLine = task?.Command,
            IsRunning = () => running
        };

        if (task is { CaptureLog: true })
        {
            session.RunAgainAsync = async () =>
            {
                running = true;
                session.MarkRunning();
                try
                {
                    await _taskScheduler.RunNowAsync(taskId).ConfigureAwait(true);
                    LoadScheduledTasks();
                    var updated = _taskScheduler.List().FirstOrDefault(t => t.Id == taskId);
                    if (updated?.LastLogPath is { Length: > 0 } newPath && File.Exists(newPath))
                    {
                        session.LogPath = newPath;
                        session.CommandLine = updated.Command;
                    }
                }
                finally
                {
                    running = false;
                    session.MarkFinished();
                    session.NotifyUpdated();
                }
            };
        }

        StackrootLogViewer.Open(
            logPath,
            $"Log — {label}",
            openInExternalEditor,
            _settingsStore,
            chrome: new SiteLogChrome(session));
    }

    private void LoadVersions()
    {
        // Load PHP versions (once)
        if (PhpVersions.Count == 0)
        {
            PhpVersions.Add(new PhpVersionOptionViewModel("no-php", "No PHP"));
            var installedIds = _catalogStore.List(PackageType.Php)
                .Select(e => e.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _catalogStore.List(PackageType.Php)
                         .Where(e => installedIds.Contains(e.Id))
                         .OrderByDescending(e => e.Id, StringComparer.OrdinalIgnoreCase))
                PhpVersions.Add(new PhpVersionOptionViewModel(entry.Id, entry.Label));
        }

        // Load Node versions (once)
        if (NodeVersions.Count == 0)
        {
            NodeVersions.Add(new NodeVersionOptionViewModel("none", "None"));
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
                catch { }
            });
        }
    }

    private void CleanupOldLoginFiles()
    {
        if (Site is null) return;
        try
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-5);
            var files = new[] {
                Path.Combine(Site.Path, "wp-instant-login.php"),
                Path.Combine(Site.Path, "stackroot-auto-login.php")
            };
            foreach (var f in files)
            {
                if (File.Exists(f) && File.GetLastWriteTimeUtc(f) < cutoff)
                    File.Delete(f);
            }
        }
        catch { }
    }

    private void RebuildQuickActions()
    {
        QuickActionGroups.Clear();
        QuickActions.Clear();
        if (Site is null)
        {
            RaisePropertyChanged(nameof(HasQuickActions));
            return;
        }

        var actions = _siteManager.GetQuickActions(Site.Id);
        foreach (var groupKey in SiteQuickActionPresets.GroupLabels.Keys)
        {
            var groupActions = actions
                .Where(action => string.Equals(action.Group, groupKey, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (groupActions.Count == 0)
            {
                continue;
            }

            var groupVm = new QuickActionGroupViewModel
            {
                Title = SiteQuickActionVisuals.ResolveGroupTitle(groupKey)
            };

            foreach (var action in groupActions)
            {
                var visual = SiteQuickActionVisuals.Resolve(action);
                var item = new QuickActionItemViewModel
                {
                    Id = action.Id,
                    Label = action.Label,
                    ShortLabel = visual.ShortLabel,
                    RuntimeBadge = visual.RuntimeBadge,
                    AccentBrush = CreateBrush(visual.AccentColor),
                    IconSource = visual.IconSource,
                    HasIcon = visual.HasIcon,
                    IsDangerous = action.Dangerous,
                    Command = new RelayCommand(_ => _ = RunQuickActionAsync(action.Id), _ => CanRunQuickAction())
                };
                groupVm.Actions.Add(item);
                QuickActions.Add(item);
            }

            QuickActionGroups.Add(groupVm);
        }

        RaisePropertyChanged(nameof(HasQuickActions));
    }

    private void RefreshLinkedDatabases()
    {
        LinkedDatabases.Clear();
        if (Site is null)
        {
            RaisePropertyChanged(nameof(HasLinkedDatabases));
            RaisePropertyChanged(nameof(ShowNoLinkedDatabases));
            return;
        }

        foreach (var database in _databaseManager.List()
                     .Where(record => string.Equals(record.SiteId, SiteId, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(record => record.Name, StringComparer.OrdinalIgnoreCase))
        {
            LinkedDatabases.Add(new SiteLinkedDatabaseViewModel
            {
                Name = database.Name,
                EngineLabel = database.Engine.ToString(),
                CopyEnvCommand = new RelayCommand(_ => CopyEnvSnippet(database)),
                ViewEnvCommand = new RelayCommand(_ => ViewEnvSnippet(database))
            });
        }

        RaisePropertyChanged(nameof(HasLinkedDatabases));
        RaisePropertyChanged(nameof(ShowNoLinkedDatabases));
    }

    private void RebuildProcessPresets()
    {
        ProcessPresets.Clear();
        if (Site is null)
        {
            RaisePropertyChanged(nameof(HasProcessPresets));
            return;
        }

        foreach (var preset in _siteManager.GetProcessPresets(Site.Id))
        {
            ProcessPresets.Add(new SiteProcessPresetViewModel
            {
                Id = preset.Id,
                Name = preset.Name,
                Description = preset.Description,
                AddCommand = new RelayCommand(
                    _ => AddProcessFromPreset(preset.Id),
                    _ => Site is not null && !IsProcessBulkBusy)
            });
        }

        RaisePropertyChanged(nameof(HasProcessPresets));
    }

    private void RefreshProcesses() => _ = RefreshProcessesAsync();

    private async Task RefreshProcessesAsync()
    {
        var siteId = SiteId;
        if (Site is null)
        {
            SiteProcesses.Clear();
            RaisePropertyChanged(nameof(HasSiteProcesses));
            RaisePropertyChanged(nameof(ShowNoSiteProcesses));
            return;
        }

        var rows = await Task.Run(() =>
            _processManager.List(siteId)
                .OrderBy(process => process.Featured == true ? 0 : 1)
                .ThenBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
                .Select(process => new SiteProcessRowViewModel
                {
                    Id = process.Id,
                    Name = process.Name,
                    RuntimeLabel = process.RuntimeLabel,
                    StatusText = FormatStatus(process),
                    StatusColor = FormatStatusColor(process),
                    CommandLine = process.CommandLine,
                    Enabled = process.Enabled,
                    AutoStart = process.AutoStart,
                    EnabledLabel = process.Enabled ? "Enabled" : "Disabled",
                    IsRunning = process.Status == ProcessStatus.Running,
                    ShowLogButton = true
                })
                .ToList()).ConfigureAwait(true);

        SiteProcesses.Clear();
        foreach (var row in rows)
        {
            SiteProcesses.Add(row);
        }

        RaisePropertyChanged(nameof(HasSiteProcesses));
        RaisePropertyChanged(nameof(ShowNoSiteProcesses));
        StartAllProcessesCommand.RaiseCanExecuteChanged();
        StopAllProcessesCommand.RaiseCanExecuteChanged();
    }

    private async Task RunQuickActionAsync(string? actionId, SiteLogSession? logSession = null)
    {
        if (!CanRunQuickAction() || string.IsNullOrWhiteSpace(actionId))
        {
            return;
        }

        var definition = SiteQuickActionPresets.Get(actionId);
        if (definition?.ConfirmMessage is { Length: > 0 } confirmMessage && logSession is null)
        {
            var confirmed = ConfirmDialog.Show(
                Application.Current?.MainWindow,
                definition.Label,
                confirmMessage,
                definition.Label,
                isDanger: definition.Dangerous);
            if (!confirmed)
            {
                return;
            }
        }

        IsRunningAction = true;
        _lastQuickActionId = actionId;
        _lastCommandLabel = definition?.Label ?? actionId;
        _lastCommandLine = string.Empty;
        _lastCommandExitCode = null;
        _lastCommandDurationMs = 0;
        _lastCommandLogPath = string.Empty;
        CommandStatusMessage = $"Running {_lastCommandLabel}…";
        ShowCommandLogButton = false;
        var siteLabel = Site?.Domain ?? SiteId;
        var progressId = _activity.Begin("Sites", SessionActivityMessages.SiteQuickActionRunning(_lastCommandLabel));
        logSession?.MarkRunning();
        try
        {
            var result = await Task.Run(() => _siteManager.RunQuickAction(
                SiteId,
                actionId,
                started =>
                {
                    void apply()
                    {
                        _lastCommandLogPath = started.LogPath;
                        _lastCommandLine = started.CommandLine;
                        ApplySiteLogStarted(null, logSession, started.LogPath, started.CommandLine);
                        ShowCommandLogButton = true;
                        RaiseCommandStatusChromeChanged();
                    }

                    if (logSession is not null)
                    {
                        ApplyOnUiThread(apply);
                    }
                    else
                    {
                        Application.Current?.Dispatcher.BeginInvoke(apply);
                    }
                }))
                .ConfigureAwait(true);

            if (!string.IsNullOrWhiteSpace(result.LogPath))
            {
                _lastCommandLogPath = result.LogPath;
                ApplySiteLogResult(null, logSession, result);
            }

            _lastCommandLine = result.CommandLine ?? string.Empty;
            _lastCommandExitCode = result.ExitCode;
            _lastCommandDurationMs = result.DurationMs;

            CommandStatusMessage = SiteQuickActionStatusFormatter.Format(actionId, definition?.Label, result);
            ShowCommandLogButton = !string.IsNullOrWhiteSpace(_lastCommandLogPath);
            var activityMessage = SessionActivityMessages.SiteQuickActionResult(siteLabel, CommandStatusMessage);
            if (result.ExitCode == 0)
            {
                _activity.Complete(progressId, "Sites", activityMessage);
            }
            else
            {
                _activity.Complete(progressId, "Sites", activityMessage, SessionActivityTone.Error);
            }
        }
        catch (Exception ex)
        {
            CommandStatusMessage = "Command failed.";
            _activity.Fail(progressId, "Sites", ex.Message, ex);
            if (string.IsNullOrWhiteSpace(_lastCommandLogPath))
            {
                var fallbackDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Stackroot",
                    "logs",
                    "sites",
                    SiteId);
                Directory.CreateDirectory(fallbackDir);
                _lastCommandLogPath = Path.Combine(fallbackDir, $"error-{DateTimeOffset.UtcNow:yyyy-MM-ddTHH-mm-ss-fffZ}.log");
                File.WriteAllText(_lastCommandLogPath, ex.ToString());
            }

            ShowCommandLogButton = true;
        }
        finally
        {
            IsRunningAction = false;
            logSession?.MarkFinished();
            logSession?.NotifyUpdated();
        }
    }

    private async Task RunCustomCommandTrackedAsync(
        SiteCustomCommandViewModel vm,
        string commandLine,
        SiteLogSession? logSession = null)
    {
        if (Site is null || vm.IsRunning)
        {
            return;
        }

        vm.IsRunning = true;
        vm.ClearLogPath();
        vm.Status = $"Running {vm.Label}…";

        if (!ActiveCommandStatuses.Contains(vm))
        {
            ActiveCommandStatuses.Add(vm);
        }

        RaisePropertyChanged(nameof(ShowCustomCommandStatus));
        DismissCustomStatusCommand.RaiseCanExecuteChanged();

        logSession?.MarkRunning();
        try
        {
            var result = await Task.Run(() => _siteManager.RunCustomCommand(
                SiteId,
                vm.Id,
                commandLine,
                started =>
                {
                    void apply() => ApplySiteLogStarted(vm, logSession, started.LogPath, started.CommandLine);
                    if (logSession is not null)
                    {
                        ApplyOnUiThread(apply);
                    }
                    else
                    {
                        Application.Current?.Dispatcher.BeginInvoke(apply);
                    }
                }))
                .ConfigureAwait(true);

            ApplySiteLogResult(vm, logSession, result);
            vm.SetCompletion(result.ExitCode, result.DurationMs);
            vm.Status = FormatCustomCommandStatus(vm.Label, result);
        }
        catch (Exception ex)
        {
            vm.Status = $"{vm.Label} failed.";
            _activity.LogError("Sites", ex.Message, ex);
            if (string.IsNullOrWhiteSpace(vm._logPath))
            {
                var fallbackDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Stackroot",
                    "logs",
                    "sites",
                    SiteId);
                Directory.CreateDirectory(fallbackDir);
                var fallbackPath = Path.Combine(fallbackDir, $"error-{DateTimeOffset.UtcNow:yyyy-MM-ddTHH-mm-ss-fffZ}.log");
                await File.WriteAllTextAsync(fallbackPath, ex.ToString()).ConfigureAwait(true);
                ApplySiteLogStarted(vm, logSession, fallbackPath, commandLine);
            }
        }
        finally
        {
            vm.IsRunning = false;
            vm.IsCancelling = false;
            DismissCustomStatusCommand.RaiseCanExecuteChanged();
            logSession?.MarkFinished();
            logSession?.NotifyUpdated();
        }
    }

    private static void ApplyOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            action();
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }

    private static void ApplySiteLogStarted(
        SiteCustomCommandViewModel? vm,
        SiteLogSession? session,
        string logPath,
        string commandLine)
    {
        vm?.SetLogPath(logPath);
        vm?.SetCommandLine(commandLine);
        if (session is null)
        {
            return;
        }

        session.CommandLine = commandLine;
        session.LogPath = logPath;
    }

    private static void ApplySiteLogResult(
        SiteCustomCommandViewModel? vm,
        SiteLogSession? session,
        SiteCommandResult result)
    {
        if (string.IsNullOrWhiteSpace(result.LogPath))
        {
            return;
        }

        vm?.SetLogPath(result.LogPath);
        if (!string.IsNullOrWhiteSpace(result.CommandLine))
        {
            vm?.SetCommandLine(result.CommandLine);
        }

        if (session is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.CommandLine))
        {
            session.CommandLine = result.CommandLine;
        }

        session.LogPath = result.LogPath;
    }

    private static string FormatCustomCommandStatus(string label, SiteCommandResult result) =>
        SiteQuickActionStatusFormatter.Format("custom", label, result);

    private Task CancelCustomCommandAsync(SiteCustomCommandViewModel vm)
    {
        if (!vm.CanCancelRunning() || string.IsNullOrWhiteSpace(vm._logPath))
        {
            return Task.CompletedTask;
        }

        vm.IsCancelling = true;
        vm.Status = "Cancelling…";
        return Task.Run(() => _siteManager.CancelSiteCommand(vm._logPath!));
    }

    private void OpenCustomCommandLog(SiteCustomCommandViewModel vm, bool openInExternalEditor)
    {
        if (string.IsNullOrWhiteSpace(vm._logPath) || !File.Exists(vm._logPath))
        {
            return;
        }

        var session = CreateCustomCommandLogSession(vm);
        StackrootLogViewer.Open(
            session.LogPath,
            $"Log — {vm.Label}",
            openInExternalEditor,
            _settingsStore,
            chrome: new SiteLogChrome(session));
    }

    private SiteLogSession CreateCustomCommandLogSession(SiteCustomCommandViewModel vm)
    {
        var session = new SiteLogSession(vm._logPath!)
        {
            CommandLine = vm.Command,
            GetCompletion = () => vm.GetCompletion()
        };
        session.CancelAsync = () => Task.Run(() => _siteManager.CancelSiteCommand(session.LogPath));
        session.IsRunning = () => session.IsMarkedRunning || _siteManager.IsSiteCommandRunning(session.LogPath);
        session.RunAgainAsync = () => RunCustomCommandTrackedAsync(vm, vm.Command, session);
        return session;
    }

    private (int ExitCode, long DurationMs)? GetLastCommandCompletion() =>
        _lastCommandExitCode is int exitCode
            ? (exitCode, _lastCommandDurationMs)
            : null;

    public void OpenCommandLog(bool openInExternalEditor = false)
    {
        if (string.IsNullOrWhiteSpace(_lastCommandLogPath) || !File.Exists(_lastCommandLogPath))
        {
            return;
        }

        var title = string.IsNullOrWhiteSpace(_lastCommandLabel)
            ? $"Log — {Path.GetFileName(_lastCommandLogPath)}"
            : $"Log — {_lastCommandLabel}";
        var session = CreateQuickActionLogSession();
        StackrootLogViewer.Open(
            session.LogPath,
            title,
            openInExternalEditor,
            _settingsStore,
            chrome: new SiteLogChrome(session));
    }

    private SiteLogSession CreateQuickActionLogSession()
    {
        var session = new SiteLogSession(_lastCommandLogPath)
        {
            CommandLine = _lastCommandLine,
            GetCompletion = () => GetLastCommandCompletion()
        };
        session.CancelAsync = () => Task.Run(() => _siteManager.CancelSiteCommand(session.LogPath));
        session.IsRunning = () => session.IsMarkedRunning || IsRunningAction || _siteManager.IsSiteCommandRunning(session.LogPath);
        if (!string.IsNullOrWhiteSpace(_lastQuickActionId))
        {
            session.RunAgainAsync = () => RunQuickActionAsync(_lastQuickActionId, session);
        }

        return session;
    }

    private bool CanCancelRunningCommand() =>
        IsRunningAction
        && !string.IsNullOrWhiteSpace(_lastCommandLogPath)
        && _siteManager.IsSiteCommandRunning(_lastCommandLogPath);

    private Task CancelRunningCommandAsync()
    {
        if (!CanCancelRunningCommand())
        {
            return Task.CompletedTask;
        }

        CommandStatusMessage = "Cancelling…";
        return Task.Run(() => _siteManager.CancelSiteCommand(_lastCommandLogPath));
    }

    private void RaiseCommandStatusChromeChanged()
    {
        RaisePropertyChanged(nameof(ShowDismissCommandStatus));
        RaisePropertyChanged(nameof(ShowCancelCommandStatus));
        CancelCommandStatusCommand.RaiseCanExecuteChanged();
        DismissCommandStatusCommand.RaiseCanExecuteChanged();
    }

    private void ClearCommandStatus()
    {
        CommandStatusMessage = string.Empty;
        ShowCommandLogButton = false;
        _lastCommandLogPath = string.Empty;
        _lastCommandLabel = string.Empty;
        _lastCommandLine = string.Empty;
        _lastCommandExitCode = null;
        _lastCommandDurationMs = 0;
        RaiseCommandStatusChromeChanged();
    }

    private void ClearProcessStatus() => ProcessStatusMessage = string.Empty;

    private void ClearDatabaseStatus() => DatabaseStatusMessage = string.Empty;

    private void SetProcessStatus(string message) => ProcessStatusMessage = message;

    private bool CanRunProcessAction(string? id) =>
        !string.IsNullOrWhiteSpace(id)
        && !IsProcessBulkBusy
        && !IsRunningAction
        && (_busyProcessId is null || _busyProcessId != id);

    private void SetBusyProcess(string? id)
    {
        _busyProcessId = string.IsNullOrWhiteSpace(id) ? null : id.Trim();
        RaiseProcessActionCanExecute();
    }

    private void RaiseProcessActionCanExecute()
    {
        StartProcessCommand.RaiseCanExecuteChanged();
        StopProcessCommand.RaiseCanExecuteChanged();
        RestartProcessCommand.RaiseCanExecuteChanged();
    }

    private void SetDatabaseStatus(string message) => DatabaseStatusMessage = message;

    private void OpenSite()
    {
        if (Site is null || !Site.Enabled)
        {
            return;
        }

        var https = Site.ForceHttps == true;
        Process.Start(new ProcessStartInfo
        {
            FileName = $"{(https ? "https" : "http")}://{Site.Domain}",
            UseShellExecute = true
        });
    }

    private void OpenFolder()
    {
        if (Site is null || string.IsNullOrWhiteSpace(Site.Path))
        {
            return;
        }

        Directory.CreateDirectory(Site.Path);
        Process.Start(new ProcessStartInfo
        {
            FileName = Site.Path,
            UseShellExecute = true
        });
    }

    private void OpenTerminal()
    {
        if (Site is null || string.IsNullOrWhiteSpace(Site.Path)) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                WorkingDirectory = Site.Path,
                UseShellExecute = true
            });
        }
        catch { }
    }

    private void CopyEnvSnippet(DatabaseRecord database)
    {
        var snippet = _databaseManager.BuildEnvSnippet(database.Name, database.Engine);
        Clipboard.SetText(snippet);
        SetDatabaseStatus($"Copied .env snippet for {database.Name}.");
    }

    private void ViewEnvSnippet(DatabaseRecord database)
    {
        var snippet = _databaseManager.BuildEnvSnippet(database.Name, database.Engine);
        MessageDialog.Show(
            Application.Current?.MainWindow,
            $".env — {database.Name}",
            "Database connection settings for this site.",
            StackrootDialogKind.Info,
            details: snippet);
    }

    private void OpenSslPathsDialog()
    {
        var ssl = DevSslCertificateManager.TryGetExisting(_paths);
        if (ssl is null)
        {
            SetProcessStatus("Dev SSL certificates are not available yet. Enable HTTPS and add at least one site.");
            return;
        }

        var dialogVm = new DevSslPathsDialogViewModel(ssl.CertAbs, ssl.KeyAbs);
        var dialog = new DevSslPathsDialog
        {
            DataContext = dialogVm,
            Owner = Application.Current?.MainWindow
        };
        dialogVm.RequestClose += (_, _) => dialog.Close();
        dialog.ShowDialog();
    }

    private void OpenEditSiteDialog()
    {
        if (Site is null)
        {
            return;
        }

        var site = Site;
        var settings = _settingsStore.Load();
        var templates = _services.GetRequiredService<IReadOnlyList<SiteTemplateDefinition>>();
        var dialogVm = new EditSiteDialogViewModel(site, templates, PhpVersions, settings.General.WwwPath, settings.NginxHttp);
        var dialog = new EditSiteDialog
        {
            DataContext = dialogVm,
            Owner = Application.Current?.MainWindow
        };

        dialogVm.RequestClose += (_, _) => dialog.Close();
        dialogVm.SiteSaved += (_, patch) =>
        {
            try
            {
                _siteManager.Update(site.Id, patch);
                _activity.LogSuccess("Sites", SessionActivityMessages.SiteUpdated(site.Domain));
                dialog.Close();
                RefreshSite();
                RefreshSiteNavigation();
                _ = RebuildNginxAsync();
            }
            catch (Exception ex)
            {
                dialogVm.ErrorMessage = ex.Message;
            }
        };

        dialog.ShowDialog();
    }

    private void ToggleFeatured()
    {
        if (Site is null)
        {
            return;
        }

        _siteManager.Update(Site.Id, new UpdateSiteInput { Featured = !IsFeatured });
        RefreshSite();
        RefreshSiteNavigation();
    }

    private async Task ToggleEnabledAsync()
    {
        if (Site is null || IsTogglingEnabled)
        {
            return;
        }

        IsTogglingEnabled = true;
        try
        {
            var targetEnabled = !SiteEnabled;
            var domain = Site.Domain;
            await Task.Run(() => _siteManager.Update(Site.Id, new UpdateSiteInput { Enabled = targetEnabled }))
                .ConfigureAwait(true);
            RefreshSite();
            _activity.LogSuccess("Sites", SessionActivityMessages.SiteEnabled(domain, targetEnabled));
            await RebuildNginxAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _activity.LogError("Sites", ex.Message, ex);
            CommandStatusMessage = ex.Message;
        }
        finally
        {
            IsTogglingEnabled = false;
        }
    }

    private void RefreshSiteNavigation() =>
        _services.GetRequiredService<ShellViewModel>().RefreshSiteNavFromStore(_siteManager);

    private async Task RebuildNginxAsync()
    {
        try
        {
            var rebuilder = _services.GetRequiredService<NginxWebStackRebuilder>();
            await rebuilder.RebuildAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _activity.LogError("Sites", ex.Message, ex);
        }
    }

    private void RaiseSiteActionChromeChanged()
    {
        RaisePropertyChanged(nameof(SiteEnabled));
        RaisePropertyChanged(nameof(SiteDisabled));
        RaisePropertyChanged(nameof(IsFeatured));
        RaisePropertyChanged(nameof(FeaturedPinToolTip));
        RaisePropertyChanged(nameof(EnableDisableLabel));
        RaisePropertyChanged(nameof(ToggleEnabledButtonLabel));
        EditSiteCommand.RaiseCanExecuteChanged();
        ToggleFeaturedCommand.RaiseCanExecuteChanged();
        ToggleEnabledCommand.RaiseCanExecuteChanged();
    }

    private void OpenCreateDatabaseDialog()
    {
        if (Site is null)
        {
            return;
        }

        var siteOptions = new List<SiteLinkOptionViewModel>
        {
            new() { SiteId = Site.Id, Label = Site.Domain }
        };

        foreach (var linkedSite in _siteManager.List().OrderBy(s => s.Domain, StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(linkedSite.Id, Site.Id, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            siteOptions.Add(new SiteLinkOptionViewModel
            {
                SiteId = linkedSite.Id,
                Label = linkedSite.Domain
            });
        }

        var dialogVm = new CreateDatabaseDialogViewModel(
            _databaseManager.ListEngines().ToList(),
            siteOptions,
            _databaseManager,
            _services.GetRequiredService<SessionActivityReporter>(),
            Site.Id);
        var owner = Application.Current?.MainWindow;
        var dialog = new CreateDatabaseDialog
        {
            DataContext = dialogVm,
            Owner = owner
        };

        dialogVm.RequestClose += (_, _) => dialog.Close();
        dialogVm.DatabaseCreated += (_, record) =>
        {
            dialog.Close();
            RefreshLinkedDatabases();
            SetDatabaseStatus($"Database '{record.Name}' created and linked to this site.");
        };
        dialog.ShowDialog();
    }

    private void AddProcessFromPreset(string? presetId)
    {
        if (Site is null || string.IsNullOrWhiteSpace(presetId))
        {
            return;
        }

        var preset = SiteProcessPresets.Get(presetId);
        if (preset is null)
        {
            return;
        }

        try
        {
            _processManager.Add(SiteProcessPresets.ToProcess(preset, Site.Id, Site.Path) with
            {
                PhpVersionId = Site.PhpVersionId
            });
            _activity.LogSuccess("Sites", SessionActivityMessages.SiteProcessPresetAdded(preset.Name));
            SetProcessStatus($"Added preset: {preset.Name}.");
            RefreshProcesses();
        }
        catch (Exception ex)
        {
            _activity.LogError("Sites", ex.Message, ex);
            SetProcessStatus(ex.Message);
        }
    }

    private void StartAllProcesses() => _ = StartAllProcessesAsync();

    private async Task StartAllProcessesAsync()
    {
        if (Site is null)
        {
            return;
        }

        IsProcessBulkBusy = true;
        try
        {
            var siteId = SiteId;
            var domain = Site.Domain;
            var results = await Task.Run(() => _processManager.StartAll(siteId)).ConfigureAwait(true);
            _activityCoordinator.NotifyProcessActions(results, "start");
            if (results.Count > 0)
            {
                _activity.LogSuccess(
                    "Sites",
                    SessionActivityMessages.SiteProcessBulkStarted(results.Count, domain));
            }

            SetProcessStatus("Started all enabled processes.");
            await RefreshProcessesAsync();
        }
        catch (Exception ex)
        {
            _activity.LogError("Sites", ex.Message, ex);
            SetProcessStatus(ex.Message);
        }
        finally
        {
            IsProcessBulkBusy = false;
        }
    }

    private void StopAllProcesses() => _ = StopAllProcessesAsync();

    private async Task StopAllProcessesAsync()
    {
        if (Site is null)
        {
            return;
        }

        IsProcessBulkBusy = true;
        try
        {
            var siteId = SiteId;
            var domain = Site.Domain;
            var results = await Task.Run(() => _processManager.StopAll(siteId)).ConfigureAwait(true);
            _activityCoordinator.NotifyProcessActions(results, "stop");
            if (results.Count > 0)
            {
                _activity.LogSuccess(
                    "Sites",
                    SessionActivityMessages.SiteProcessBulkStopped(results.Count, domain));
            }

            SetProcessStatus("Stopped all processes.");
            await RefreshProcessesAsync();
        }
        catch (Exception ex)
        {
            _activity.LogError("Sites", ex.Message, ex);
            SetProcessStatus(ex.Message);
        }
        finally
        {
            IsProcessBulkBusy = false;
        }
    }

    private async Task StartProcessAsync(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        SetBusyProcess(id);
        try
        {
            var result = await Task.Run(() => _processManager.Start(id)).ConfigureAwait(true);
            _activityCoordinator.NotifyProcessAction(result, "start");
            await RefreshProcessesAsync();
        }
        catch (Exception ex)
        {
            _activity.LogError("Sites", ex.Message, ex);
            SetProcessStatus(ex.Message);
        }
        finally
        {
            SetBusyProcess(null);
        }
    }

    private async Task StopProcessAsync(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        SetBusyProcess(id);
        try
        {
            var result = await Task.Run(() => _processManager.Stop(id)).ConfigureAwait(true);
            _activityCoordinator.NotifyProcessAction(result, "stop");
            await RefreshProcessesAsync();
        }
        catch (Exception ex)
        {
            _activity.LogError("Sites", ex.Message, ex);
            SetProcessStatus(ex.Message);
        }
        finally
        {
            SetBusyProcess(null);
        }
    }

    private async Task RestartProcessAsync(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        SetBusyProcess(id);
        try
        {
            var result = await Task.Run(() =>
            {
                _processManager.Stop(id);
                Thread.Sleep(300);
                return _processManager.Start(id);
            }).ConfigureAwait(true);
            _activityCoordinator.NotifyProcessAction(result, "restart");
            await RefreshProcessesAsync();
        }
        catch (Exception ex)
        {
            _activity.LogError("Sites", ex.Message, ex);
            SetProcessStatus(ex.Message);
        }
        finally
        {
            SetBusyProcess(null);
        }
    }

    private void ToggleProcessEnabled(SiteProcessRowViewModel? row) =>
        _ = ToggleProcessEnabledAsync(row);

    private async Task ToggleProcessEnabledAsync(SiteProcessRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            var enabled = !row.Enabled;
            await Task.Run(() => _processManager.SetEnabled(row.Id, enabled)).ConfigureAwait(true);
            _activity.LogSuccess("Sites", SessionActivityMessages.SiteProcessEnabled(row.Name, enabled));
            SetProcessStatus(enabled ? "Process enabled." : "Process disabled.");
            await RefreshProcessesAsync();
        }
        catch (Exception ex)
        {
            _activity.LogError("Sites", ex.Message, ex);
            SetProcessStatus(ex.Message);
        }
    }

    private void RemoveProcess(SiteProcessRowViewModel? row) =>
        _ = RemoveProcessAsync(row);

    private async Task RemoveProcessAsync(SiteProcessRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (!ConfirmDialog.Show(
                Application.Current?.MainWindow,
                "Remove process?",
                $"Remove {row.Name} from this site?",
                "Remove",
                isDanger: true))
        {
            return;
        }

        try
        {
            await Task.Run(() => _processManager.Remove(row.Id)).ConfigureAwait(true);
            _activity.LogInfo("Sites", SessionActivityMessages.ProcessRemoved(row.Name));
            SetProcessStatus($"Removed {row.Name}.");
            await RefreshProcessesAsync();
        }
        catch (Exception ex)
        {
            _activity.LogError("Sites", ex.Message, ex);
            SetProcessStatus(ex.Message);
        }
    }

    private void OpenAddProcessDialog()
    {
        if (Site is null)
        {
            return;
        }

        var dialogVm = ActivatorUtilities.CreateInstance<AddGlobalProcessDialogViewModel>(_services);
        dialogVm.SiteId = Site.Id;
        dialogVm.WorkDir = Site.Path;

        var owner = Application.Current?.MainWindow;
        var dialog = new AddGlobalProcessDialog
        {
            DataContext = dialogVm,
            Owner = owner
        };

        dialogVm.RequestClose += (_, _) => dialog.Close();
        dialogVm.Saved += (_, _) =>
        {
            SetProcessStatus("Process saved.");
            RefreshProcesses();
        };
        dialog.ShowDialog();
    }

    private void OpenEditProcess(SiteProcessRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var process = _processManager.List(SiteId).FirstOrDefault(candidate => string.Equals(candidate.Id, row.Id, StringComparison.Ordinal));
        if (process is null)
        {
            return;
        }

        var dialogVm = ActivatorUtilities.CreateInstance<AddGlobalProcessDialogViewModel>(_services, process);
        var owner = Application.Current?.MainWindow;
        var dialog = new AddGlobalProcessDialog
        {
            DataContext = dialogVm,
            Owner = owner
        };

        dialogVm.RequestClose += (_, _) => dialog.Close();
        dialogVm.Saved += (_, _) =>
        {
            SetProcessStatus("Process updated.");
            RefreshProcesses();
        };
        dialog.ShowDialog();
    }

    private void OpenProcessLog(SiteProcessRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var dialogVm = new SiteProcessLogDialogViewModel(_processManager, row.Id, row.Name);
        var owner = Application.Current?.MainWindow;
        var dialog = new SiteProcessLogDialog
        {
            DataContext = dialogVm,
            Owner = owner
        };

        dialogVm.RequestClose += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => dialogVm.Dispose();
        dialog.Show();
    }

    private bool CanRunQuickAction() => Site is not null && !IsRunningAction;

    private void RaiseQuickActionCanExecute()
    {
        RunQuickActionCommand.RaiseCanExecuteChanged();
        foreach (var action in QuickActions)
        {
            action.Command.RaiseCanExecuteChanged();
        }
    }

    private static string FormatStatus(ProcessInfo process)
    {
        if (process.Status is ProcessStatus.Running or ProcessStatus.Restarting)
        {
            return process.Status == ProcessStatus.Restarting ? "Restarting" : "Running";
        }

        if (!process.Available)
        {
            return "Unavailable";
        }

        return process.Status switch
        {
            ProcessStatus.Error => "Error",
            _ => "Stopped"
        };
    }

    private static string FormatStatusColor(ProcessInfo process) =>
        process.Status switch
        {
            ProcessStatus.Running => "#8FD6B6",
            ProcessStatus.Restarting => "#9BB8F0",
            ProcessStatus.Error => "#EAAAB0",
            _ => "#91A0B5"
        };

    private static System.Windows.Media.SolidColorBrush CreateBrush(string hex) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);

    private async Task InstallSiteAsync()
    {
        if (Site is null || !ShowInstallerButton) return;

        WordPressInstallConfig? wpConfig = null;
        LaravelInstallConfig? laravelConfig = null;

        // Show configuration dialog based on template type
        if (Site.Template == SiteTemplateIds.Laravel)
        {
            var dialogResult = await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                Views.LaravelInstallDialog.Show(
                    System.Windows.Application.Current.MainWindow,
                    Site.Name,
                    Site.Domain));

            if (dialogResult is null) return;

            laravelConfig = new LaravelInstallConfig
            {
                StarterKit = dialogResult.StarterKit,
                Stack = dialogResult.Stack,
                DatabaseEngine = dialogResult.DatabaseEngine,
                RunNpmBuild = dialogResult.RunNpmBuild,
                RunMigrations = dialogResult.RunMigrations
            };
        }
        else if (Site.Template == SiteTemplateIds.Wordpress)
        {
            var dialogResult = await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                Views.WordPressInstallDialog.Show(
                    System.Windows.Application.Current.MainWindow,
                    Site.Name,
                    Site.Domain));

            if (dialogResult is null) return;

            wpConfig = new WordPressInstallConfig
            {
                SiteTitle = dialogResult.SiteTitle,
                AdminUser = dialogResult.AdminUser,
                AdminPassword = dialogResult.AdminPassword,
                AdminEmail = dialogResult.AdminEmail,
                DatabaseEngine = dialogResult.DatabaseEngine
            };
        }

        // Pre-check: database engine must be installed (skip for SQLite)
        var dbEngine = wpConfig?.DatabaseEngine ?? laravelConfig?.DatabaseEngine;
        if (dbEngine is not null && dbEngine != SqlEngine.Sqlite)
        {
            var engineId = dbEngine == SqlEngine.Mariadb ? ServiceId.Mariadb
                : dbEngine == SqlEngine.Postgresql ? ServiceId.Postgresql
                : ServiceId.Mysql;
            var settings = _settingsStore.Load();
            if (!settings.Services.TryGetValue(engineId, out var svc) || string.IsNullOrWhiteSpace(svc.PackageId))
            {
                StackrootDialogs.ShowWarning(
                    System.Windows.Application.Current?.MainWindow,
                    "Database not installed",
                    $"Install {dbEngine} from Services first, then try again.",
                    "Install");
                return;
            }

            if (!IsDatabasePortOpen(svc))
            {
                StackrootDialogs.ShowWarning(
                    System.Windows.Application.Current?.MainWindow,
                    "Database not running",
                    $"{dbEngine} is not running. Start it from Services, then try again.",
                    "Install");
                return;
            }
        }

        IsInstalling = true;
        InstallStatus = "Starting installation...";
        var activityId = _activity.Begin("Sites", $"Installing {Site.Domain}...");

        try
        {
            var dbName = wpConfig is not null ? SanitizeDbName(Site.Domain)
                : laravelConfig is not null ? SanitizeDbName(Site.Domain) : null;

            // Check if database already exists
            if (dbEngine != SqlEngine.Sqlite && !string.IsNullOrWhiteSpace(dbName))
            {
                var existing = _databaseManager.List()
                    .FirstOrDefault(db => string.Equals(db.Name, dbName, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    var dialogResult = System.Windows.MessageBox.Show(
                        $"Database '{dbName}' already exists.\n\nDrop and recreate it?",
                        "Database exists",
                        System.Windows.MessageBoxButton.YesNo,
                        System.Windows.MessageBoxImage.Warning);
                    if (dialogResult != System.Windows.MessageBoxResult.Yes)
                    {
                        _activity.Fail(activityId, "Sites", "Installation cancelled");
                        InstallStatus = "Installation cancelled.";
                        IsInstalling = false;
                        return;
                    }
                    _databaseManager.Delete(existing.Name, dropFromServer: true);
                }
            }

            var options = new SiteInstallOptions
            {
                CreateDatabase = dbEngine != SqlEngine.Sqlite,
                DatabaseName = dbName,
                WordPress = wpConfig,
                Laravel = laravelConfig
            };

            var result = await _siteManager.InstallSiteAsync(Site, options, msg =>
            {
                _diagnostics.LogActivity("Installer", msg.Text);
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    InstallStatus = msg.Text;
                    if (msg.Kind is InstallerMessageKind.Info
                        or InstallerMessageKind.Progress
                        or InstallerMessageKind.Success
                        or InstallerMessageKind.Warning)
                    {
                        _activity.UpdateProgress(activityId, "Sites", msg.Text);
                    }
                });
            }, System.Threading.CancellationToken.None);

            if (!result.Success)
            {
                var detail = result.PostInstallTips?.FirstOrDefault();
                _activity.Fail(activityId, "Sites", detail ?? "Installation failed");
                InstallStatus = detail ?? "Installation failed. Check the logs for details.";
                return;
            }

            RaisePropertyChanged(nameof(ShowInstallerButton));
            RaisePropertyChanged(nameof(ShowPostInstallCard));
            RaisePropertyChanged(nameof(PostInstallWpVersion));
            RaisePropertyChanged(nameof(PostInstallWpLabel));
            RaisePropertyChanged(nameof(PostInstallLaravelVersion));

            _activity.Complete(activityId, "Sites", $"{Site.Domain} installed", SessionActivityTone.Success);
            InstallStatus = "Installation complete.";
            RefreshSite();

            // Toast notification
            _services.GetRequiredService<IToastService>()?.Show(
                "Site installed",
                $"{Site.Domain} is ready!");

            // Auto-capture thumbnail
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000); // wait for nginx/php to settle
                await CaptureThumbnailAsync(forceRefresh: false);
            });
        }
        catch (Exception ex)
        {
            _activity.Fail(activityId, "Sites", $"Install failed: {ex.Message}");
            InstallStatus = $"Failed: {ex.Message}";
            _diagnostics.LogActivity("Sites", $"Installer failed: {ex.Message}");
        }
        finally
        {
            IsInstalling = false;
        }
    }

    private static string SanitizeDbName(string domain)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in domain)
        {
            if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
            else sb.Append('_');
        }
        var name = sb.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(name) ? "site_db" : name;
    }

    private void OpenPostInstallAdmin()
    {
        if (Site is null) return;

        var token = Guid.NewGuid().ToString("N");
        var fileName = "wp-instant-login.php";
        var scriptPath = Path.Combine(Site.Path, fileName);

        var script = @"<?php
@unlink(__FILE__);
if (!isset($_GET['auth']) || $_GET['auth'] !== '" + token + @"') { die('Unauthorized access.'); }
require_once('wp-load.php');
require_once('wp-includes/pluggable.php');
$user = get_user_by('id', 1);
if ($user && !is_wp_error($user)) {
    wp_clear_auth_cookie();
    wp_set_current_user($user->ID);
    wp_set_auth_cookie($user->ID);
    wp_safe_redirect(admin_url());
    exit;
}
die('Admin user not found.');
";
        File.WriteAllText(scriptPath, script);
        OpenUrl("http://" + Site.Domain + "/wp-instant-login.php?auth=" + token);
    }

    private string CredentialsPath => Site is not null
        ? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Stackroot", "sites", Site.Id, "wp-credentials.json")
        : "";

    private void LoadCredentials()
    {
        var path = CredentialsPath;
        if (!File.Exists(path)) return;
        try
        {
            var json = File.ReadAllText(path);
            var document = System.Text.Json.JsonSerializer.Deserialize<WpCredentialsDocument>(json, JsonSerializerConfig.Default);
            _postInstallPassword = document?.Password ?? "";
        }
        catch { _postInstallPassword = ""; }
    }

    private async Task ChangePasswordAsync()
    {
        if (Site is null) return;
        try
        {
            // Ask user for new password
            var newPassword = await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                ShowChangePasswordDialog());

            if (string.IsNullOrWhiteSpace(newPassword)) return;

            var wpCli = _services.GetRequiredService<WpCliManager>();
            var registry = _services.GetRequiredService<InstallRegistryStore>();
            var phpExe = ResolvePhpExe(registry);
            if (phpExe is null) return;

            var (exit, output) = await wpCli.RunAsync(phpExe, Site.Path,
                "user update admin --user_pass=" + newPassword,
                null, System.Threading.CancellationToken.None);

            if (exit == 0)
            {
                _postInstallPassword = newPassword;
                RaisePropertyChanged(nameof(PostInstallAdminPassword));
                RaisePropertyChanged(nameof(PostInstallPasswordDisplay));

                var dir = Path.GetDirectoryName(CredentialsPath);
                if (dir is not null) Directory.CreateDirectory(dir);
                var document = new WpCredentialsDocument
                {
                    SchemaVersion = DataDocumentSchemas.SiteWpCredentials,
                    Password = newPassword,
                    StorageFormat = "plain"
                };
                var credsJson = System.Text.Json.JsonSerializer.Serialize(document, JsonSerializerConfig.Default);
                File.WriteAllText(CredentialsPath, credsJson);
            }
        }
        catch { }
    }

    private bool IsDatabasePortOpen(ServicePortSettings svc)
    {
        var host = string.IsNullOrWhiteSpace(svc.Host) ? "127.0.0.1" : svc.Host;
        var port = svc.Port > 0 ? svc.Port : 3306;
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            client.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(2));
            return true;
        }
        catch { return false; }
    }

    private static string? ShowChangePasswordDialog()
    {
        var dialog = new Views.ChangePasswordDialog();
        var owner = System.Windows.Application.Current?.MainWindow;
        if (owner is not null) dialog.Owner = owner;
        return dialog.ShowDialog() == true ? dialog.Password : null;
    }

    private async Task CaptureThumbnailAsync(bool forceRefresh)
    {
        if (Site is null || !Site.Enabled || IsCapturing) return;

        IsCapturing = true;
        try
        {
            var url = (Site.ForceHttps == true ? "https" : "http") + "://" + Site.Domain;
            var path = ThumbnailPath;

            var result = await _thumbnailService.CaptureAsync(url, path, forceRefresh);

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                RaisePropertyChanged(nameof(ThumbnailImage));
                RaisePropertyChanged(nameof(HasThumbnail));
                RaisePropertyChanged(nameof(HasNoThumbnail));
                RaisePropertyChanged(nameof(ThumbnailsFeatureEnabled));
                RaisePropertyChanged(nameof(ShowCaptureButton));
                RaisePropertyChanged(nameof(ShowRefreshButton));
                RaisePropertyChanged(nameof(ThumbnailPath));
            });
        }
        catch (Exception ex)
        {
            _diagnostics?.LogActivity("Thumbnails", $"Capture failed: {ex.Message}");
        }
        finally
        {
            IsCapturing = false;
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true };
            System.Diagnostics.Process.Start(psi);
        }
        catch { }
    }

    private static string? ResolvePhpExe(InstallRegistryStore registry)
    {
        foreach (var pkg in registry.List(PackageType.Php).OrderByDescending(p => p.Id))
        {
            var exe = new[] { Path.Combine(pkg.InstallPath, "php.exe"), Path.Combine(pkg.InstallPath, "bin", "php.exe") }
                .FirstOrDefault(File.Exists);
            if (exe is not null) return exe;
        }
        return null;
    }
}

public sealed class SiteProcessRowViewModel
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string RuntimeLabel { get; init; } = string.Empty;
    public string StatusText { get; init; } = string.Empty;
    public string StatusColor { get; init; } = "#91A0B5";
    public string CommandLine { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public bool AutoStart { get; init; }
    public string EnabledLabel { get; init; } = "Enabled";
    public bool IsRunning { get; init; }
    public bool ShowLogButton { get; init; } = true;

    public bool IsLive => IsRunning;
    public bool ShowStartButton => !IsRunning;
    public bool ShowStopButton => IsRunning;
    public bool ShowRestartButton => IsRunning;

    public System.Windows.Media.Brush IndicatorColor => CreateBrush(
        IsRunning ? "#4CAE8C" :
        StatusText == "Error" ? "#E88A92" :
        StatusText == "Restarting" ? "#E9BD5B" :
        "#91A0B5");

    public System.Windows.Media.Brush RowBorderBrush => CreateBrush(
        IsRunning ? "#4D4CAE8C" :
        StatusText == "Error" ? "#59E88A92" :
        StatusText == "Restarting" ? "#66E9BD5B" :
        "#263348");

    private static System.Windows.Media.SolidColorBrush CreateBrush(string hex) =>
        new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);
}

public sealed class SiteLinkedDatabaseViewModel
{
    public string Name { get; init; } = string.Empty;
    public string EngineLabel { get; init; } = string.Empty;
    public RelayCommand CopyEnvCommand { get; init; } = new(_ => { });
    public RelayCommand ViewEnvCommand { get; init; } = new(_ => { });
}

public sealed class SiteProcessPresetViewModel
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public RelayCommand AddCommand { get; init; } = new(_ => { });
}

public sealed class QuickActionGroupViewModel
{
    public string Title { get; init; } = string.Empty;
    public ObservableCollection<QuickActionItemViewModel> Actions { get; } = [];
}

public sealed class SiteCustomCommandViewModel : ViewModelBase
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string SitePath { get; set; } = string.Empty;
    public string? ForegroundHex { get; set; }
    public string? BackgroundHex { get; set; }
    public string? IconFilePath { get; set; }
    public Action<bool>? OpenLogAction { get; set; }

    public bool HasCustomChrome =>
        CustomCommandChromeHelper.HasCustomChrome(ForegroundHex, BackgroundHex, IconFilePath);

    public System.Windows.Media.Brush? CustomForegroundBrush =>
        CustomCommandChromeHelper.TryBrush(ForegroundHex);

    public System.Windows.Media.Brush? CustomBackgroundBrush =>
        CustomCommandChromeHelper.TryBrush(BackgroundHex);

    public System.Windows.Media.ImageSource? IconSource =>
        CustomCommandChromeHelper.TryIcon(IconFilePath);

    public bool HasIcon => IconSource is not null;

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (SetProperty(ref _isRunning, value))
            {
                if (!value)
                {
                    IsCancelling = false;
                }

                RaisePropertyChanged(nameof(DisplayLabel));
                RaiseChromeChanged();
                RunCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsCancelling
    {
        get => _isCancelling;
        set
        {
            if (SetProperty(ref _isCancelling, value))
            {
                RaiseChromeChanged();
            }
        }
    }

    public string DisplayLabel => IsRunning ? $"⏳ {Label}" : Label;

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                RaisePropertyChanged(nameof(HasStatus));
                RaiseChromeChanged();
            }
        }
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(_status);
    public bool ShowDismissStatus => HasStatus && !IsRunning;
    public bool ShowCancelStatus => IsRunning && (IsCancelling || CanCancelRunning());
    public bool CanCancelCommand => ShowCancelStatus && !IsCancelling;

    public string CancelButtonLabel => IsCancelling ? "Cancelling…" : "Cancel";

    public bool ShowViewLogButton => !string.IsNullOrWhiteSpace(_logPath);

    internal string? _logPath;
    private int? _lastExitCode;
    private long _lastDurationMs;
    private bool _isCancelling;
    public Func<bool>? IsCommandRunning { get; set; }
    public Action? ChromeChanged { get; set; }

    public RelayCommand RunCommand { get; set; } = new(_ => { });
    public RelayCommand CancelCommand { get; set; } = new(_ => { });
    public RelayCommand ViewLogCommand { get; set; } = new(_ => { });

    public void SetLogPath(string logPath)
    {
        _logPath = logPath;
        RaisePropertyChanged(nameof(ShowViewLogButton));
        ViewLogCommand.RaiseCanExecuteChanged();
        RaiseChromeChanged();
    }

    public void SetCommandLine(string commandLine) => Command = commandLine;

    public void SetCompletion(int exitCode, long durationMs)
    {
        _lastExitCode = exitCode;
        _lastDurationMs = durationMs;
    }

    public (int ExitCode, long DurationMs)? GetCompletion() =>
        _lastExitCode is int code ? (code, _lastDurationMs) : null;

    public void ClearLogPath()
    {
        _logPath = null;
        _lastExitCode = null;
        _lastDurationMs = 0;
        RaisePropertyChanged(nameof(ShowViewLogButton));
        ViewLogCommand.RaiseCanExecuteChanged();
        RaiseChromeChanged();
    }

    public void ClearStatus()
    {
        Status = string.Empty;
        ClearLogPath();
    }

    public bool CanCancelRunning() =>
        IsRunning
        && !string.IsNullOrWhiteSpace(_logPath)
        && (IsCommandRunning?.Invoke() ?? true);

    public void RaiseChromeChanged()
    {
        RaisePropertyChanged(nameof(ShowDismissStatus));
        RaisePropertyChanged(nameof(ShowCancelStatus));
        RaisePropertyChanged(nameof(CanCancelCommand));
        RaisePropertyChanged(nameof(CancelButtonLabel));
        RaisePropertyChanged(nameof(ShowViewLogButton));
        CancelCommand.RaiseCanExecuteChanged();
        ViewLogCommand.RaiseCanExecuteChanged();
        ChromeChanged?.Invoke();
    }

    public void OpenLog(bool openInExternalEditor = false) =>
        OpenLogAction?.Invoke(openInExternalEditor);
}

public sealed class QuickActionItemViewModel
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string ShortLabel { get; init; } = string.Empty;
    public string RuntimeBadge { get; init; } = string.Empty;
    public System.Windows.Media.Brush AccentBrush { get; init; } = System.Windows.Media.Brushes.Gray;
    public System.Windows.Media.ImageSource? IconSource { get; init; }
    public bool HasIcon { get; init; }
    public bool IsDangerous { get; init; }
    public RelayCommand Command { get; init; } = new(_ => { });
}
