using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Stackroot.App.Commands;
using Stackroot.App.Localization;
using Stackroot.Core.Sites.Management;
using Stackroot.Core.Sites.Models;

namespace Stackroot.App.ViewModels;

public sealed class SiteRowViewModel : ViewModelBase
{
    private readonly SiteManager _siteManager;
    private readonly Action _onChanged;
    private readonly Action<Site> _openManage;
    private readonly Action<Site> _openEdit;
    private readonly Action<Site> _deleteSite;
    private readonly Action<SiteRowViewModel, string?> _onPhpChanged;
    private readonly Action<Site> _openCreateDatabase;
    private readonly Action<Site> _backupSite;
    private readonly Action<Site> _onRuntimeChanged;
    private Site _site;
    private bool _isUpdatingPhp;
    private bool _isToggling;
    private bool _isBackingUp;

    public SiteRowViewModel(
        Site site,
        SiteManager siteManager,
        Action onChanged,
        Action<Site> openManage,
        Action<Site> openEdit,
        Action<Site> deleteSite,
        Action<SiteRowViewModel, string?> onPhpChanged,
        Action<Site> openCreateDatabase,
        Action<Site> backupSite,
        Action<Site> onRuntimeChanged)
    {
        _site = site;
        _siteManager = siteManager;
        _onChanged = onChanged;
        _openManage = openManage;
        _openEdit = openEdit;
        _deleteSite = deleteSite;
        _onPhpChanged = onPhpChanged;
        _openCreateDatabase = openCreateDatabase;
        _backupSite = backupSite;
        _onRuntimeChanged = onRuntimeChanged;

        TogglePinCommand = new RelayCommand(_ => TogglePin(), _ => !_isBackingUp);
        ManageCommand = new RelayCommand(_ => _openManage(_site));
        EditCommand = new RelayCommand(_ => _openEdit(_site), _ => !_isBackingUp);
        DeleteCommand = new RelayCommand(_ => _deleteSite(_site), _ => !_isBackingUp);
        BackupCommand = new RelayCommand(_ => _backupSite(_site), _ => !_isBackingUp);
        ToggleEnabledCommand = new RelayCommand(_ => _ = ToggleEnabledAsync(), _ => !_isToggling && !_isBackingUp);
        OpenCommand = new RelayCommand(_ => OpenSite(false));
        OpenHttpsCommand = new RelayCommand(_ => OpenSite(true), _ => _site.Enabled && _site.ForceHttps == true);
        OpenFolderCommand = new RelayCommand(_ => OpenFolder());
        CreateDatabaseCommand = new RelayCommand(_ => _openCreateDatabase(_site), _ => RequiresPhp && !_isBackingUp);
    }

    public Site Site => _site;

    public string Domain => _site.Domain;
    public string Name => _site.Name;
    public string Template => _site.Template;
    public string PathSummary => _site.Path;
    public bool SiteIsEnabled => _site.Enabled;
    public bool SiteIsDisabled => !_site.Enabled;
    public string EnableDisableLabel => SiteIsEnabled
        ? LocalizationManager.Get("Loc.Common.Disable", "Disable")
        : LocalizationManager.Get("Loc.Common.Enable", "Enable");

    public bool IsBackingUp => _isBackingUp;
    public bool IsNotBackingUp => !_isBackingUp;
    public bool IsRowActionEnabled => !_isToggling && !_isBackingUp;
    public bool IsPhpEnabled => RequiresPhp && !_isBackingUp;

    public void SetBackingUp(bool value)
    {
        if (_isBackingUp == value) return;
        _isBackingUp = value;
        RaisePropertyChanged(nameof(IsBackingUp));
        RaisePropertyChanged(nameof(IsNotBackingUp));
        RaisePropertyChanged(nameof(IsRowActionEnabled));
        RaisePropertyChanged(nameof(IsPhpEnabled));
        TogglePinCommand.RaiseCanExecuteChanged();
        ToggleEnabledCommand.RaiseCanExecuteChanged();
        EditCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        BackupCommand.RaiseCanExecuteChanged();
        CreateDatabaseCommand.RaiseCanExecuteChanged();
    }

    public bool IsToggling
    {
        get => _isToggling;
        private set
        {
            if (SetProperty(ref _isToggling, value))
            {
                RaisePropertyChanged(nameof(IsNotToggling));
                RaisePropertyChanged(nameof(IsRowActionEnabled));
                RaisePropertyChanged(nameof(ToggleButtonLabel));
                ToggleEnabledCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsNotToggling => !IsToggling;
    public string ToggleButtonLabel => IsToggling
        ? (SiteIsEnabled ? LocalizationManager.Get("Loc.Common.Disabling", "Disabling…") : LocalizationManager.Get("Loc.Common.Enabling", "Enabling…"))
        : EnableDisableLabel; 

    public bool IsFeatured => _site.Featured == true;
    public string PinGlyph => IsFeatured ? "★" : "☆";
    public bool RequiresPhp => !string.IsNullOrWhiteSpace(_site.PhpVersionId);
    public bool RequiresNode => !string.IsNullOrWhiteSpace(_site.NodeVersionId);
    public bool ShowDatabaseButton => RequiresPhp;

    public RelayCommand CreateDatabaseCommand { get; }
    public RelayCommand BackupCommand { get; }

    public string SelectedPhpVersionId
    {
        get => _site.PhpVersionId ?? "no-php";
        set
        {
            if (_isUpdatingPhp || _isBackingUp || string.Equals(value, SelectedPhpVersionId, StringComparison.Ordinal))
            {
                return;
            }

            _onPhpChanged(this, value);
        }
    }

    public string SelectedNodeVersionId
    {
        get => _site.NodeVersionId ?? "none";
        set
        {
            if (_isUpdatingPhp || _isBackingUp) return;
            if (string.Equals(value, SelectedNodeVersionId, StringComparison.Ordinal)) return;

            _site = _siteManager.Update(_site.Id, new UpdateSiteInput { NodeVersionId = value == "none" ? null : value });
            UpdateSite(_site);
        }
    }

    public RelayCommand TogglePinCommand { get; }
    public RelayCommand ManageCommand { get; }
    public RelayCommand EditCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand ToggleEnabledCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand OpenHttpsCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    public void UpdateSite(Site site)
    {
        _isUpdatingPhp = true;
        try
        {
            _site = site;
            RaisePropertyChanged(nameof(Domain));
            RaisePropertyChanged(nameof(Name));
            RaisePropertyChanged(nameof(Template));
            RaisePropertyChanged(nameof(PathSummary));
            RaisePropertyChanged(nameof(SiteIsEnabled));
            RaisePropertyChanged(nameof(SiteIsDisabled));
            RaisePropertyChanged(nameof(EnableDisableLabel));
            RaisePropertyChanged(nameof(IsFeatured));
            RaisePropertyChanged(nameof(PinGlyph));
            RaisePropertyChanged(nameof(RequiresPhp));
            RaisePropertyChanged(nameof(RequiresNode));
            RaisePropertyChanged(nameof(ShowDatabaseButton));
            RaisePropertyChanged(nameof(SelectedPhpVersionId));
            OpenHttpsCommand.RaiseCanExecuteChanged();
        }
        finally
        {
            _isUpdatingPhp = false;
        }
    }

    private void TogglePin()
    {
        _site = _siteManager.Update(_site.Id, new UpdateSiteInput { Featured = !IsFeatured });
        _onChanged();
    }

    private async Task ToggleEnabledAsync()
    {
        if (_isToggling) return;
        IsToggling = true;
        try
        {
            var targetEnabled = !SiteIsEnabled;
            await Task.Run(() =>
            {
                _site = _siteManager.Update(_site.Id, new UpdateSiteInput { Enabled = targetEnabled });
            });
            UpdateSite(_site);
            _onRuntimeChanged(_site);
        }
        finally
        {
            IsToggling = false;
        }
    }

    private void OpenSite(bool https)
    {
        var scheme = https ? "https" : "http";
        Process.Start(new ProcessStartInfo
        {
            FileName = $"{scheme}://{_site.Domain}",
            UseShellExecute = true
        });
    }

    private void OpenFolder()
    {
        var path = _site.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }
}

public sealed class SiteGroupViewModel
{
    public SiteGroupViewModel(string title, string? hint, IEnumerable<SiteRowViewModel> sites)
    {
        Title = title;
        Hint = hint;
        Sites = new ObservableCollection<SiteRowViewModel>(sites);
    }

    public string Title { get; }
    public string? Hint { get; }
    public bool ShowHint => !string.IsNullOrWhiteSpace(Hint);
    public ObservableCollection<SiteRowViewModel> Sites { get; }
}
