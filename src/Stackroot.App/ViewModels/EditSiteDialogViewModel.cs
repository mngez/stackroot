using System.Collections.ObjectModel;

using System.Windows;

using System.Windows.Forms;

using Stackroot.App.Commands;

using Stackroot.App.Views;

using Stackroot.Core.Sites;

using Stackroot.Core.Sites.Models;

using NginxHttpSettings = Stackroot.Core.Abstractions.NginxHttpSettings;



namespace Stackroot.App.ViewModels;

public sealed class EditSiteDialogViewModel : ViewModelBase

{

    private readonly Site _site;

    private readonly string _configuredWwwPath;

    private readonly NginxHttpSettings _nginxHttp;

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

        string? configuredWwwPath,

        NginxHttpSettings? nginxHttp = null)

    {

        _site = site;

        _configuredWwwPath = configuredWwwPath ?? string.Empty;

        _nginxHttp = nginxHttp ?? new NginxHttpSettings();

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

            DevProxies.Add(new DevProxyRowViewModel(proxy, RemoveProxy, RaiseProxySummaryChanged, _nginxHttp));

        }



        SaveCommand = new RelayCommand(_ => Submit(), _ => CanSubmit());

        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty));

        AddProxyCommand = new RelayCommand(_ => AddProxy());
        AddRegexProxyCommand = new RelayCommand(_ => AddRegexProxy());

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
    public RelayCommand AddRegexProxyCommand { get; }

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



        DevProxies.Add(new DevProxyRowViewModel(null, RemoveProxy, RaiseProxySummaryChanged, _nginxHttp, expand: true));

        RaiseProxySummaryChanged();

    }

    private void AddRegexProxy()
    {
        foreach (var proxy in DevProxies)
        {
            proxy.IsExpanded = false;
        }

        DevProxies.Add(new DevProxyRowViewModel(
            null,
            RemoveProxy,
            RaiseProxySummaryChanged,
            _nginxHttp,
            expand: true,
            defaultKind: SiteDevProxyLocationKind.Regex));

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


