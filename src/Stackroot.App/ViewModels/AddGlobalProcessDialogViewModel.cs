using System.Collections.ObjectModel;
using Stackroot.App.Commands;
using Stackroot.App.Helpers;
using Stackroot.App.Services;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;
using Stackroot.Core.Settings;
using Stackroot.Core.Sites.Management;
using Stackroot.Core.Supervisor;

namespace Stackroot.App.ViewModels;

public sealed class AddGlobalProcessDialogViewModel : ViewModelBase
{
    private readonly GlobalProcessManager _processManager;
    private readonly GlobalProcessStore _processStore;
    private readonly ProcessArgvResolver _argvResolver;
    private readonly SiteManager _siteManager;
    private readonly InstallRegistryStore _registryStore;
    private readonly PackageCatalogStore _catalogStore;
    private readonly SettingsStore _settingsStore;
    private readonly SessionActivityReporter _activity;
    private readonly ProcessInfo? _editProcess;

    private string _name = string.Empty;
    private string _command = string.Empty;
    private SiteCommandRuntime _runtime = SiteCommandRuntime.Shell;
    private string _workDir = string.Empty;
    private string _workingDirectory = ".";
    private string? _siteId;
    private string? _selectedPhpVersionId;
    private bool _enabled = true;
    private bool _autoStart;
    private bool _featured;
    private string _statusMessage = string.Empty;
    private bool _isSaving;

    public AddGlobalProcessDialogViewModel(
        GlobalProcessManager processManager,
        GlobalProcessStore processStore,
        ProcessArgvResolver argvResolver,
        SiteManager siteManager,
        InstallRegistryStore registryStore,
        PackageCatalogStore catalogStore,
        SettingsStore settingsStore,
        SessionActivityReporter activity,
        ProcessInfo? editProcess = null)
    {
        _processManager = processManager;
        _processStore = processStore;
        _argvResolver = argvResolver;
        _siteManager = siteManager;
        _registryStore = registryStore;
        _catalogStore = catalogStore;
        _settingsStore = settingsStore;
        _activity = activity;
        _editProcess = editProcess;

        Title = editProcess is null ? "Add process" : "Edit process";
        IsEdit = editProcess is not null;

        RuntimeOptions =
        [
            new RuntimeOptionViewModel(SiteCommandRuntime.Shell, "Shell / any command"),
            new RuntimeOptionViewModel(SiteCommandRuntime.Php, "PHP"),
            new RuntimeOptionViewModel(SiteCommandRuntime.Composer, "Composer"),
            new RuntimeOptionViewModel(SiteCommandRuntime.Npm, "npm (Node)"),
            new RuntimeOptionViewModel(SiteCommandRuntime.Node, "Node"),
            new RuntimeOptionViewModel(SiteCommandRuntime.Python, "Python")
        ];

        Sites = new ObservableCollection<SiteOptionViewModel>(
        [
            new SiteOptionViewModel(string.Empty, "None — app-wide process"),
            ..siteManager.List()
                .OrderBy(site => site.Name, StringComparer.OrdinalIgnoreCase)
                .Select(site => new SiteOptionViewModel(site.Id, $"{site.Name} ({site.Domain})"))
        ]);

        PhpVersions = new ObservableCollection<PhpVersionOptionViewModel>();
        LoadPhpVersions();

        SaveCommand = new RelayCommand(_ => Save(), _ => !IsSaving);
        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty));

        if (editProcess is not null)
        {
            LoadEdit(editProcess);
        }
        else
        {
            SyncDefaultPhpVersion(force: true);
        }
    }

    public string Title { get; }
    public bool IsEdit { get; }
    public IReadOnlyList<RuntimeOptionViewModel> RuntimeOptions { get; }
    public ObservableCollection<SiteOptionViewModel> Sites { get; }
    public ObservableCollection<PhpVersionOptionViewModel> PhpVersions { get; }

    public bool ShowPhpVersionPicker =>
        Runtime is SiteCommandRuntime.Php or SiteCommandRuntime.Composer;

    public bool ShowProjectFolder => string.IsNullOrWhiteSpace(SiteId);

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string Command
    {
        get => _command;
        set => SetProperty(ref _command, value);
    }

    public SiteCommandRuntime Runtime
    {
        get => _runtime;
        set
        {
            if (!SetProperty(ref _runtime, value))
            {
                return;
            }

            RaisePropertyChanged(nameof(ShowPhpVersionPicker));
            SyncDefaultPhpVersion(force: true);
        }
    }

    public string WorkDir
    {
        get => _workDir;
        set => SetProperty(ref _workDir, value);
    }

    public string WorkingDirectory
    {
        get => _workingDirectory;
        set => SetProperty(ref _workingDirectory, value);
    }

    public string? SiteId
    {
        get => _siteId;
        set
        {
            if (!SetProperty(ref _siteId, value))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                var site = _siteManager.Get(value);
                if (site is not null)
                {
                    WorkDir = site.Path;
                }
            }

            RaisePropertyChanged(nameof(ShowProjectFolder));
            SyncDefaultPhpVersion(force: true);
        }
    }

    public string? SelectedPhpVersionId
    {
        get => _selectedPhpVersionId;
        set => SetProperty(ref _selectedPhpVersionId, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public bool AutoStart
    {
        get => _autoStart;
        set => SetProperty(ref _autoStart, value);
    }

    public bool Featured
    {
        get => _featured;
        set => SetProperty(ref _featured, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (SetProperty(ref _isSaving, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public RelayCommand SaveCommand { get; }
    public RelayCommand CloseCommand { get; }

    public event EventHandler? RequestClose;
    public event EventHandler? Saved;

    private void LoadEdit(ProcessInfo process)
    {
        var stored = _processStore.GetById(process.Id);
        var argv = stored?.Argv ?? process.Argv;

        Name = process.Name;
        Command = ProcessArgvResolver.FormatCommandDisplay(process.Runtime, argv);
        Runtime = process.Runtime;
        WorkDir = !string.IsNullOrWhiteSpace(process.WorkDir)
            ? process.WorkDir
            : (!string.IsNullOrWhiteSpace(process.SiteId)
                ? _siteManager.Get(process.SiteId)?.Path ?? string.Empty
                : string.Empty);
        WorkingDirectory = string.IsNullOrWhiteSpace(process.Cwd) ? "." : process.Cwd;
        SiteId = process.SiteId;
        Enabled = process.Enabled;
        AutoStart = process.AutoStart;
        Featured = process.Featured == true;
        SelectedPhpVersionId = process.PhpVersionId;
        SyncDefaultPhpVersion(force: string.IsNullOrWhiteSpace(process.PhpVersionId));
    }

    private void LoadPhpVersions()
    {
        PhpVersions.Clear();
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

    private void SyncDefaultPhpVersion(bool force = false)
    {
        if (!ShowPhpVersionPicker)
        {
            return;
        }

        if (!force && !string.IsNullOrWhiteSpace(SelectedPhpVersionId))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(SiteId))
        {
            var site = _siteManager.Get(SiteId);
            if (!string.IsNullOrWhiteSpace(site?.PhpVersionId)
                && PhpVersions.Any(option => string.Equals(option.Id, site.PhpVersionId, StringComparison.OrdinalIgnoreCase)))
            {
                SelectedPhpVersionId = site.PhpVersionId;
                return;
            }
        }

        var activeVersionId = _settingsStore.Load().Php.ActiveVersionId;
        if (!string.IsNullOrWhiteSpace(activeVersionId)
            && PhpVersions.Any(option => string.Equals(option.Id, activeVersionId, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedPhpVersionId = activeVersionId;
        }
        else
        {
            SelectedPhpVersionId = PhpVersions.FirstOrDefault()?.Id;
        }
    }

    private string ResolveStoredWorkDir(string? siteId)
    {
        if (!string.IsNullOrWhiteSpace(siteId))
        {
            return _siteManager.Get(siteId)?.Path ?? WorkDir.Trim();
        }

        return WorkDir.Trim();
    }

    private static string NormalizeStoredCwd(string workingDirectory)
    {
        var trimmed = workingDirectory.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? "." : trimmed;
    }

    private void Save()
    {
        StatusMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(Name))
        {
            StatusMessage = "Name is required.";
            return;
        }

        if (string.IsNullOrWhiteSpace(SiteId) && string.IsNullOrWhiteSpace(WorkDir))
        {
            StatusMessage = "Select a linked site or project folder.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Command))
        {
            StatusMessage = "Command is required.";
            return;
        }

        if (ShowPhpVersionPicker && string.IsNullOrWhiteSpace(SelectedPhpVersionId))
        {
            StatusMessage = "Select a PHP version.";
            return;
        }

        IsSaving = true;
        try
        {
            var trimmedSiteId = string.IsNullOrWhiteSpace(SiteId) ? null : SiteId;
            var trimmedWorkDir = ResolveStoredWorkDir(trimmedSiteId);
            var trimmedCwd = NormalizeStoredCwd(WorkingDirectory);
            var phpVersionId = ShowPhpVersionPicker ? SelectedPhpVersionId : null;
            var normalizedCommand = _argvResolver.NormalizeUserCommand(Runtime, Command.Trim());
            var resolvedArgv = _argvResolver.BuildArgv(Runtime, normalizedCommand, trimmedSiteId, trimmedWorkDir, phpVersionId);
            if (resolvedArgv.Count == 0)
            {
                StatusMessage = "Command could not be parsed.";
                return;
            }

            var storedArgv = Runtime == SiteCommandRuntime.Shell
                ? resolvedArgv
                : normalizedCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

            var process = new GlobalProcess
            {
                Name = Name.Trim(),
                Runtime = Runtime,
                Argv = storedArgv,
                WorkDir = trimmedWorkDir,
                SiteId = trimmedSiteId,
                Cwd = trimmedCwd,
                Enabled = Enabled,
                AutoStart = AutoStart,
                Featured = Featured,
                PhpVersionId = phpVersionId
            };

            if (IsEdit && _editProcess is not null)
            {
                var existing = _processStore.GetById(_editProcess.Id);
                if (existing is not null)
                {
                    process = process with
                    {
                        Description = existing.Description,
                        FromPreset = existing.FromPreset,
                        NodeVersion = existing.NodeVersion,
                    };
                }

                _processManager.Update(_editProcess.Id, process);
            }
            else
            {
                _processManager.Add(process);
            }

            _activity.LogSuccess(
                "Processes",
                SessionActivityMessages.ProcessSaved(process.Name.Trim(), !IsEdit));
            Saved?.Invoke(this, EventArgs.Empty);
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _activity.LogError("Processes", ex.Message, ex);
            StatusMessage = ex.Message;
        }
        finally
        {
            IsSaving = false;
        }
    }
}

public sealed record RuntimeOptionViewModel(SiteCommandRuntime Runtime, string Label);

public sealed record SiteOptionViewModel(string Id, string Label);
