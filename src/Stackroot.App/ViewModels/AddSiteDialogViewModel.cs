using System.Collections.ObjectModel;
using System.Windows.Forms;
using Stackroot.App.Commands;
using Stackroot.Core.Sites;
using Stackroot.Core.Sites.Models;

namespace Stackroot.App.ViewModels;

public sealed record PhpVersionOptionViewModel(string Id, string Label);
public sealed record NodeVersionOptionViewModel(string Id, string Label);

public sealed class SiteTemplateCardViewModel : ViewModelBase
{
    private readonly Action<string> _onSelected;

    public SiteTemplateCardViewModel(SiteTemplateDefinition definition, bool isSelected, Action<string> onSelected)
    {
        Id = definition.Id;
        Label = definition.Label;
        DocumentRoot = definition.DocumentRoot;
        RequiresPhp = true;
        _isSelected = isSelected;
        _onSelected = onSelected;
        SelectCommand = new RelayCommand(_ => _onSelected(Id));
    }

    public string Id { get; }
    public string Label { get; }
    public string DocumentRoot { get; }
    public bool RequiresPhp { get; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public RelayCommand SelectCommand { get; }
}

public sealed class AddSiteDialogViewModel : ViewModelBase
{
    private readonly IReadOnlyList<SiteTemplateDefinition> _templates;
    private readonly string _configuredWwwPath;
    private string _name = string.Empty;
    private string _domain = string.Empty;
    private string _domainSuffix = "test";
    private string _domainAliasesText = string.Empty;
    private string _selectedTemplate = SiteTemplateIds.Static;
    private string _selectedPhpVersionId = "no-php";
    private string _selectedNodeVersionId = "none";
    private string _pathMode = "default";
    private string _customPath = string.Empty;
    private string _pathPreview = string.Empty;
    private string _errorMessage = string.Empty;
    private bool _goToSiteDashboard = true;
    private bool _isCreating;

    public AddSiteDialogViewModel(
        IReadOnlyList<SiteTemplateDefinition> templates,
        IEnumerable<PhpVersionOptionViewModel> phpVersions,
        IEnumerable<NodeVersionOptionViewModel> nodeVersions,
        string? configuredWwwPath)
    {
        _templates = templates;
        _configuredWwwPath = configuredWwwPath ?? string.Empty;
        PhpVersions = new ObservableCollection<PhpVersionOptionViewModel>(phpVersions);
        NodeVersions = new ObservableCollection<NodeVersionOptionViewModel>(nodeVersions);
        TemplateCards = new ObservableCollection<SiteTemplateCardViewModel>();

        foreach (var template in templates)
        {
            TemplateCards.Add(new SiteTemplateCardViewModel(
                template,
                template.Id == _selectedTemplate,
                SelectTemplate));
        }

        CreateCommand = new RelayCommand(_ => Submit(), _ => CanSubmit());
        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty));
        BrowseFolderCommand = new RelayCommand(_ => BrowseFolder());

        RefreshPathPreview();
    }

    public ObservableCollection<SiteTemplateCardViewModel> TemplateCards { get; }
    public ObservableCollection<PhpVersionOptionViewModel> PhpVersions { get; }
    public ObservableCollection<NodeVersionOptionViewModel> NodeVersions { get; }

    public string SelectedNodeVersionId
    {
        get => _selectedNodeVersionId;
        set => SetProperty(ref _selectedNodeVersionId, value);
    }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                CreateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string Domain
    {
        get => _domain;
        set
        {
            if (SetProperty(ref _domain, value))
            {
                RaisePropertyChanged(nameof(PreviewDomain));
                RefreshPathPreview();
                CreateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string DomainSuffix
    {
        get => _domainSuffix;
        set
        {
            if (SetProperty(ref _domainSuffix, value))
            {
                RaisePropertyChanged(nameof(PreviewDomain));
                RefreshPathPreview();
            }
        }
    }

    public string DomainAliasesText
    {
        get => _domainAliasesText;
        set => SetProperty(ref _domainAliasesText, value);
    }

    public string SelectedPhpVersionId
    {
        get => _selectedPhpVersionId;
        set => SetProperty(ref _selectedPhpVersionId, value);
    }

    public string PathPreview
    {
        get => _pathPreview;
        private set => SetProperty(ref _pathPreview, value);
    }

    public bool IsDefaultPathMode
    {
        get => _pathMode == "default";
        set
        {
            if (value)
            {
                PathMode = "default";
            }
        }
    }

    public bool IsCustomPathMode
    {
        get => _pathMode == "custom";
        set
        {
            if (value)
            {
                PathMode = "custom";
            }
        }
    }

    public string CustomPath
    {
        get => _customPath;
        set
        {
            if (SetProperty(ref _customPath, value))
            {
                RefreshPathPreview();
                CreateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string PreviewDomain
    {
        get
        {
            var sub = Domain.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(sub))
            {
                return string.Empty;
            }

            if (sub.Contains('.'))
            {
                return sub;
            }

            var suffix = DomainSuffix.Trim().ToLowerInvariant();
            return string.IsNullOrWhiteSpace(suffix) ? sub : $"{sub}.{suffix}";
        }
    }

    public string DocumentRootLabel => SiteTemplates.Resolve(_selectedTemplate).DocumentRoot;
    public bool PhpSelectorEnabled => true;

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

    public bool GoToSiteDashboard
    {
        get => _goToSiteDashboard;
        set => SetProperty(ref _goToSiteDashboard, value);
    }

    public bool IsCreating
    {
        get => _isCreating;
        private set
        {
            if (SetProperty(ref _isCreating, value))
            {
                RaisePropertyChanged(nameof(CreateButtonText));
                CreateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string CreateButtonText => IsCreating ? "Creating…" : "Add domain";

    public RelayCommand CreateCommand { get; }
    public RelayCommand CloseCommand { get; }
    public RelayCommand BrowseFolderCommand { get; }

    public event EventHandler<CreateSiteInput>? SiteCreated;
    public event EventHandler? RequestClose;

    private string PathMode
    {
        get => _pathMode;
        set
        {
            if (SetProperty(ref _pathMode, value))
            {
                RaisePropertyChanged(nameof(IsDefaultPathMode));
                RaisePropertyChanged(nameof(IsCustomPathMode));
                RefreshPathPreview();
                CreateCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private void SelectTemplate(string templateId)
    {
        _selectedTemplate = templateId;
        foreach (var card in TemplateCards)
        {
            card.IsSelected = card.Id == templateId;
        }

        if (SelectedPhpVersionId == "no-php" && PhpVersions.FirstOrDefault(v => v.Id != "no-php") is { } first)
        {
            SelectedPhpVersionId = first.Id;
        }

        RaisePropertyChanged(nameof(DocumentRootLabel));
        RaisePropertyChanged(nameof(PhpSelectorEnabled));
        CreateCommand.RaiseCanExecuteChanged();
    }

    private bool CanSubmit()
    {
        if (string.IsNullOrWhiteSpace(Domain.Trim()))
        {
            return false;
        }

        return !IsCustomPathMode || !string.IsNullOrWhiteSpace(CustomPath.Trim());
    }

    private void RefreshPathPreview()
    {
        var preview = SitePaths.Preview(BuildPathInput(), _configuredWwwPath);
        PathPreview = preview.Path;
    }

    private CreateSiteInput BuildPathInput() => new()
    {
        Domain = Domain.Trim(),
        DomainSuffix = DomainSuffix.Trim(),
        PathMode = _pathMode,
        CustomPath = string.IsNullOrWhiteSpace(CustomPath) ? null : CustomPath.Trim()
    };

    private void BrowseFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select site folder",
            UseDescriptionForTitle = true,
            SelectedPath = string.IsNullOrWhiteSpace(CustomPath)
                ? PathPreview
                : CustomPath
        };

        if (dialog.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            CustomPath = dialog.SelectedPath;
            PathMode = "custom";
        }
    }

    private void Submit()
    {
        ErrorMessage = string.Empty;
        var domainInput = Domain.Trim();
        if (string.IsNullOrWhiteSpace(domainInput))
        {
            ErrorMessage = "Enter a domain.";
            return;
        }

        if (IsCustomPathMode && string.IsNullOrWhiteSpace(CustomPath.Trim()))
        {
            ErrorMessage = "Choose a folder for the site.";
            return;
        }

        var preview = PreviewDomain;
        var aliasError = SiteDomainNames.ValidateAliases(
            preview,
            SiteDomainNames.ParseAliasesText(DomainAliasesText));
        if (aliasError is not null)
        {
            ErrorMessage = aliasError;
            return;
        }

        IsCreating = true;

        SiteCreated?.Invoke(this, new CreateSiteInput
        {
            Name = string.IsNullOrWhiteSpace(Name) ? preview : Name.Trim(),
            Domain = domainInput,
            DomainSuffix = DomainSuffix.Trim(),
            DomainAliases = SiteDomainNames.ParseAliasesText(DomainAliasesText),
            Template = _selectedTemplate,
            PhpVersionId = SelectedPhpVersionId == "no-php" ? null : SelectedPhpVersionId,
            NodeVersionId = SelectedNodeVersionId == "none" ? null : SelectedNodeVersionId,
            PathMode = _pathMode,
            CustomPath = IsCustomPathMode ? CustomPath.Trim() : null
        });
    }
}
