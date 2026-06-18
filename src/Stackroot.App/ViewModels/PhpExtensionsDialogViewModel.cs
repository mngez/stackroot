using System.Collections.ObjectModel;
using Stackroot.App.Commands;
using Stackroot.Core.Services.Php;

namespace Stackroot.App.ViewModels;

public sealed class PhpExtensionsDialogViewModel : ViewModelBase
{
    private readonly PhpExtensionManager _extensionManager;
    private readonly PeclInstaller _peclInstaller;
    private readonly string _versionId;
    private string _versionLabel = string.Empty;
    private string _filter = "all";
    private string _searchQuery = string.Empty;
    private string _statusMessage = string.Empty;
    private bool _isBusy;
    private string? _togglingExtensionId;
    private string? _installingPeclId;

    public PhpExtensionsDialogViewModel(
        PhpExtensionManager extensionManager,
        PeclInstaller peclInstaller,
        string versionId,
        string versionLabel)
    {
        _extensionManager = extensionManager;
        _peclInstaller = peclInstaller;
        _versionId = versionId;
        _versionLabel = versionLabel;

        Extensions = [];
        InstallablePecl = [];

        ToggleExtensionCommand = new RelayCommand(
            arg => ToggleExtension(arg as PhpExtensionRowViewModel),
            arg => !IsBusy && arg is PhpExtensionRowViewModel row && row.CanToggle);
        InstallPeclCommand = new RelayCommand(
            arg => _ = InstallPeclAsync(arg as InstallablePeclRowViewModel),
            arg => !IsBusy && arg is InstallablePeclRowViewModel);
        EnableAllReadyCommand = new RelayCommand(_ => EnableAllReady(), _ => !IsBusy);
        ResetExtensionsCommand = new RelayCommand(_ => ResetExtensions(), _ => !IsBusy);
        ShowAllCommand = new RelayCommand(_ => Filter = "all");
        ShowEnabledCommand = new RelayCommand(_ => Filter = "enabled");
        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty));

        Reload();
    }

    public ObservableCollection<PhpExtensionRowViewModel> Extensions { get; }
    public ObservableCollection<InstallablePeclRowViewModel> InstallablePecl { get; }

    public string VersionLabel
    {
        get => _versionLabel;
        private set => SetProperty(ref _versionLabel, value);
    }

    public string VersionId => _versionId;

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value))
            {
                ApplyFilter();
            }
        }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                ApplyFilter();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                ToggleExtensionCommand.RaiseCanExecuteChanged();
                InstallPeclCommand.RaiseCanExecuteChanged();
                EnableAllReadyCommand.RaiseCanExecuteChanged();
                ResetExtensionsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public int TotalCount => _allExtensions.Count;
    public int EnabledCount => _allExtensions.Count(e => e.Effective);

    public RelayCommand ToggleExtensionCommand { get; }
    public RelayCommand InstallPeclCommand { get; }
    public RelayCommand EnableAllReadyCommand { get; }
    public RelayCommand ResetExtensionsCommand { get; }
    public RelayCommand ShowAllCommand { get; }
    public RelayCommand ShowEnabledCommand { get; }
    public RelayCommand CloseCommand { get; }

    public event EventHandler? RequestClose;
    public event EventHandler? SettingsChanged;

    private readonly List<PhpExtensionRowViewModel> _allExtensions = [];

    private void Reload()
    {
        _allExtensions.Clear();
        foreach (var state in _extensionManager.ListExtensionStates(_versionId))
        {
            _allExtensions.Add(new PhpExtensionRowViewModel(state));
        }

        InstallablePecl.Clear();
        foreach (var pecl in _extensionManager.ListInstallablePecl(_versionId))
        {
            InstallablePecl.Add(new InstallablePeclRowViewModel
            {
                Id = pecl.Id,
                Label = pecl.Label,
                Description = pecl.Description ?? string.Empty
            });
        }

        ApplyFilter();
        RaisePropertyChanged(nameof(TotalCount));
        RaisePropertyChanged(nameof(EnabledCount));
    }

    private void ApplyFilter()
    {
        Extensions.Clear();
        var query = SearchQuery.Trim();
        IEnumerable<PhpExtensionRowViewModel> rows = Filter == "enabled"
            ? _allExtensions.Where(e => e.Effective)
            : _allExtensions;

        if (!string.IsNullOrWhiteSpace(query))
        {
            rows = rows.Where(row =>
                row.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Description.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var row in rows)
        {
            Extensions.Add(row);
        }
    }

    private void ToggleExtension(PhpExtensionRowViewModel? row)
    {
        if (row is null || IsBusy)
        {
            return;
        }

        _togglingExtensionId = row.Name;
        row.IsBusy = true;
        try
        {
            _extensionManager.ToggleExtension(_versionId, row.Name, !row.Enabled);
            Reload();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            StatusMessage = row.Enabled ? $"Disabled {row.Label}." : $"Enabled {row.Label}.";
        }
        finally
        {
            row.IsBusy = false;
            _togglingExtensionId = null;
        }
    }

    private void EnableAllReady()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            _extensionManager.EnableAllReady(_versionId);
            Reload();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            StatusMessage = "Enabled all ready extensions.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ResetExtensions()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            _extensionManager.ResetExtensions(_versionId);
            Reload();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            StatusMessage = "Extensions reset to defaults.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallPeclAsync(InstallablePeclRowViewModel? row)
    {
        if (row is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        _installingPeclId = row.Id;
        row.IsInstalling = true;
        try
        {
            await _peclInstaller.InstallAsync(row.Id, _versionId, (message, percent) =>
            {
                StatusMessage = $"{message} ({percent}%)";
            });
            _extensionManager.MarkExtensionEnabled(_versionId, row.Id);
            Reload();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            StatusMessage = $"{row.Label} installed and enabled.";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            row.IsInstalling = false;
            _installingPeclId = null;
            IsBusy = false;
        }
    }
}

public sealed class PhpExtensionRowViewModel : ViewModelBase
{
    private bool _isBusy;

    public PhpExtensionRowViewModel(PhpExtensionState state)
    {
        Name = state.Name;
        Label = state.Label;
        Description = state.Description ?? string.Empty;
        Enabled = state.Enabled;
        Effective = state.Effective;
        Available = state.Available;
        Kind = state.Kind;
        BlockedReason = state.BlockedReason ?? string.Empty;
        CanToggle = state.CanToggle;
        StatusText = state.Effective
            ? "Active in ini"
            : state.BlockedReason ?? (state.Enabled ? "Enabled (not loaded)" : "Off");
    }

    public string Name { get; }
    public string Label { get; }
    public string Description { get; }
    public string Kind { get; }
    public string BlockedReason { get; }
    public string StatusText { get; }

    public bool Enabled { get; }
    public bool Effective { get; }
    public bool Available { get; }
    public bool CanToggle { get; }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }
}

public sealed class InstallablePeclRowViewModel : ViewModelBase
{
    private bool _isInstalling;

    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;

    public bool IsInstalling
    {
        get => _isInstalling;
        set => SetProperty(ref _isInstalling, value);
    }
}
