using System.Collections.ObjectModel;

using System.Windows;

using System.Windows.Forms;

using Stackroot.App.Commands;

using Stackroot.App.Views;

using Stackroot.Core.Sites;

using Stackroot.Core.Sites.Models;



namespace Stackroot.App.ViewModels;



public sealed class DevProxyRowViewModel : ViewModelBase

{

    private readonly Action<DevProxyRowViewModel> _onRemove;

    private readonly Action _onEnabledChanged;

    private string _name = string.Empty;

    private string _locationPath = "/";

    private string _targetUrl = string.Empty;

    private bool _enabled = true;

    private bool _websocket;

    private bool _isExpanded;



    public DevProxyRowViewModel(
        SiteDevProxy? source,
        Action<DevProxyRowViewModel> onRemove,
        Action onEnabledChanged,
        bool expand = false)

    {

        _onRemove = onRemove;

        _onEnabledChanged = onEnabledChanged;

        Id = source?.Id ?? Guid.NewGuid().ToString("N");

        _name = source?.Name ?? string.Empty;

        _locationPath = source?.LocationPath ?? "/";

        _targetUrl = source?.TargetUrl ?? string.Empty;

        _enabled = source?.Enabled ?? false;

        _websocket = source?.Websocket ?? false;

        _isExpanded = expand;

        RemoveCommand = new RelayCommand(_ => _onRemove(this));

    }



    public string Id { get; }



    public string Name

    {

        get => _name;

        set

        {

            if (SetProperty(ref _name, value))

            {

                RaisePropertyChanged(nameof(HeaderText));

            }

        }

    }



    public string LocationPath

    {

        get => _locationPath;

        set => SetProperty(ref _locationPath, value);

    }



    public string TargetUrl

    {

        get => _targetUrl;

        set => SetProperty(ref _targetUrl, value);

    }



    public bool Enabled

    {

        get => _enabled;

        set

        {

            if (SetProperty(ref _enabled, value))

            {

                RaisePropertyChanged(nameof(StatusHint));

                _onEnabledChanged();

            }

        }

    }



    public bool Websocket

    {

        get => _websocket;

        set => SetProperty(ref _websocket, value);

    }



    public bool IsExpanded

    {

        get => _isExpanded;

        set => SetProperty(ref _isExpanded, value);

    }



    public string HeaderText => string.IsNullOrWhiteSpace(Name) ? "New proxy" : Name.Trim();



    public string StatusHint => Enabled ? "Active" : "Disabled";



    public RelayCommand RemoveCommand { get; }



    public SiteDevProxy ToModel() => new()

    {

        Id = Id,

        Name = Name.Trim(),

        LocationPath = LocationPath.Trim(),

        TargetUrl = TargetUrl.Trim(),

        Enabled = Enabled,

        Websocket = Websocket

    };



    public string? Validate()

    {

        if (string.IsNullOrWhiteSpace(Name))

        {

            return "Name is required.";

        }



        if (string.IsNullOrWhiteSpace(LocationPath))

        {

            return "Location path is required.";

        }



        if (string.IsNullOrWhiteSpace(TargetUrl))

        {

            return "Target URL is required.";

        }



        if (!Uri.TryCreate(TargetUrl.Trim(), UriKind.Absolute, out var uri) ||

            uri.Scheme is not "http" and not "https")

        {

            return "Target URL must use http:// or https://.";

        }



        return null;

    }

}



public sealed class EditSiteDialogViewModel : ViewModelBase

{

    private readonly Site _site;

    private readonly string _configuredWwwPath;

    private string _selectedTemplate;

    private string _name;

    private string _path;

    private string _selectedPhpVersionId;

    private bool _enabled;

    private bool _forceHttps;

    private string _domainAliasesText = string.Empty;

    private string _errorMessage = string.Empty;



    public EditSiteDialogViewModel(

        Site site,

        IReadOnlyList<SiteTemplateDefinition> templates,

        IEnumerable<PhpVersionOptionViewModel> phpVersions,

        string? configuredWwwPath)

    {

        _site = site;

        _configuredWwwPath = configuredWwwPath ?? string.Empty;

        Domain = site.Domain;

        _domainAliasesText = SiteDomainNames.FormatAliasesText(site.DomainAliases);

        _path = site.Path;

        _name = site.Name;

        _selectedTemplate = site.Template;

        _selectedPhpVersionId = site.PhpVersionId ?? "no-php";

        _enabled = site.Enabled;

        _forceHttps = site.ForceHttps == true;



        PhpVersions = new ObservableCollection<PhpVersionOptionViewModel>(phpVersions);

        TemplateCards = new ObservableCollection<SiteTemplateCardViewModel>();

        DevProxies = new ObservableCollection<DevProxyRowViewModel>();



        foreach (var template in templates)

        {

            TemplateCards.Add(new SiteTemplateCardViewModel(

                template,

                template.Id == _selectedTemplate,

                SelectTemplate));

        }



        foreach (var proxy in site.DevProxies ?? [])

        {

            DevProxies.Add(new DevProxyRowViewModel(proxy, RemoveProxy, RaiseProxySummaryChanged));

        }



        SaveCommand = new RelayCommand(_ => Submit(), _ => CanSubmit());

        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty));

        AddProxyCommand = new RelayCommand(_ => AddProxy());

        BrowseFolderCommand = new RelayCommand(_ => BrowseFolder());

    }



    public string Domain { get; }



    public string DomainAliasesText

    {

        get => _domainAliasesText;

        set => SetProperty(ref _domainAliasesText, value);

    }



    public ObservableCollection<SiteTemplateCardViewModel> TemplateCards { get; }

    public ObservableCollection<PhpVersionOptionViewModel> PhpVersions { get; }

    public ObservableCollection<DevProxyRowViewModel> DevProxies { get; }



    public string Path

    {

        get => _path;

        set
        {
            if (SetProperty(ref _path, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
            }
        }

    }



    public string Name

    {

        get => _name;

        set
        {
            if (SetProperty(ref _name, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
            }
        }

    }



    public string SelectedPhpVersionId

    {

        get => _selectedPhpVersionId;

        set
        {
            if (SetProperty(ref _selectedPhpVersionId, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
            }
        }

    }



    public bool Enabled

    {

        get => _enabled;

        set => SetProperty(ref _enabled, value);

    }



    public bool ForceHttps

    {

        get => _forceHttps;

        set => SetProperty(ref _forceHttps, value);

    }



    public string DocumentRootLabel => SiteTemplates.Resolve(_selectedTemplate).DocumentRoot;

    public bool PhpSelectorEnabled => true;

    public int EnabledProxyCount => DevProxies.Count(proxy => proxy.Enabled);

    public bool ShowEmptyProxies => DevProxies.Count == 0;



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



    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);



    public RelayCommand SaveCommand { get; }

    public RelayCommand CloseCommand { get; }

    public RelayCommand AddProxyCommand { get; }

    public RelayCommand BrowseFolderCommand { get; }



    public event EventHandler<UpdateSiteInput>? SiteSaved;

    public event EventHandler? RequestClose;



    private void SelectTemplate(string templateId)

    {

        _selectedTemplate = templateId;

        foreach (var card in TemplateCards)

        {

            card.IsSelected = card.Id == templateId;

        }



        if (PhpSelectorEnabled)

        {

            if (SelectedPhpVersionId == "no-php" && PhpVersions.FirstOrDefault(v => v.Id != "no-php") is { } first)

            {

                SelectedPhpVersionId = first.Id;

            }

        }

        else

        {

            SelectedPhpVersionId = "no-php";

        }



        RaisePropertyChanged(nameof(DocumentRootLabel));

        RaisePropertyChanged(nameof(PhpSelectorEnabled));

        SaveCommand.RaiseCanExecuteChanged();

    }



    private void AddProxy()

    {

        foreach (var proxy in DevProxies)

        {

            proxy.IsExpanded = false;

        }



        DevProxies.Add(new DevProxyRowViewModel(null, RemoveProxy, RaiseProxySummaryChanged, expand: true));

        RaiseProxySummaryChanged();

    }



    private void RemoveProxy(DevProxyRowViewModel proxy)

    {

        if (!ConfirmDialog.Show(
                System.Windows.Application.Current?.MainWindow,
                "Remove dev proxy?",
                $"Remove \"{proxy.Name}\" from this site?",
                "Remove",
                isDanger: true))
        {
            return;
        }

        DevProxies.Remove(proxy);

        RaiseProxySummaryChanged();

    }

    private void RaiseProxySummaryChanged()

    {

        RaisePropertyChanged(nameof(EnabledProxyCount));

        RaisePropertyChanged(nameof(ShowEmptyProxies));

    }



    private void BrowseFolder()

    {

        using var dialog = new FolderBrowserDialog

        {

            Description = "Select site folder",

            UseDescriptionForTitle = true,

            SelectedPath = string.IsNullOrWhiteSpace(Path) ? _site.Path : Path

        };



        if (dialog.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))

        {

            Path = dialog.SelectedPath;

        }

    }



    private string ResolvePathMode(string trimmedPath)

    {

        var defaultPath = System.IO.Path.Combine(SitePaths.EffectiveWwwPath(_configuredWwwPath), Domain);

        return string.Equals(trimmedPath, defaultPath, StringComparison.OrdinalIgnoreCase)

            ? "default"

            : "custom";

    }

    private bool CanSubmit()

    {

        return !string.IsNullOrWhiteSpace(Name)

            && !string.IsNullOrWhiteSpace(Path)

            && (!PhpSelectorEnabled || SelectedPhpVersionId != "no-php");

    }



    private void Submit()

    {

        ErrorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(Name))

        {

            ErrorMessage = "Enter a site name.";

            return;

        }



        var trimmedPath = Path.Trim();

        if (string.IsNullOrWhiteSpace(trimmedPath))

        {

            ErrorMessage = "Enter a site folder.";

            return;

        }



        if (PhpSelectorEnabled && SelectedPhpVersionId == "no-php")

        {

            ErrorMessage = "Select a PHP version for this template.";

            return;

        }



        var aliasError = SiteDomainNames.ValidateAliases(

            Domain,

            SiteDomainNames.ParseAliasesText(DomainAliasesText));

        if (aliasError is not null)

        {

            ErrorMessage = aliasError;

            return;

        }



        foreach (var proxy in DevProxies)

        {

            var proxyError = proxy.Validate();

            if (proxyError is not null)

            {

                ErrorMessage = $"{proxy.Name}: {proxyError}";

                return;

            }

        }



        SiteSaved?.Invoke(this, new UpdateSiteInput

        {

            Name = Name.Trim(),

            Template = _selectedTemplate,

            Enabled = Enabled,

            ForceHttps = ForceHttps,

            PhpVersionId = SelectedPhpVersionId,

            Path = trimmedPath,

            PathMode = ResolvePathMode(trimmedPath),

            DomainAliases = SiteDomainNames.ParseAliasesText(DomainAliasesText),

            DevProxies = DevProxies.Select(proxy => proxy.ToModel()).ToList()

        });

    }

}


