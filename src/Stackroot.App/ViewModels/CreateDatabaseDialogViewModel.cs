using System.Collections.ObjectModel;
using Stackroot.App.Commands;
using Stackroot.App.Helpers;
using Stackroot.App.Services;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Databases;
using Stackroot.Core.Databases.Models;

namespace Stackroot.App.ViewModels;

public sealed class CreateDatabaseDialogViewModel : ViewModelBase
{
    private readonly DatabaseManager _databaseManager;
    private readonly SessionActivityReporter _activity;
    private string _name = string.Empty;
    private SqlEngine _selectedEngine = SqlEngine.Mysql;
    private SiteLinkOptionViewModel? _selectedSite;
    private string _errorMessage = string.Empty;
    private string _busyMessage = string.Empty;
    private bool _isBusy;

    public CreateDatabaseDialogViewModel(
        IReadOnlyList<SqlEngine> engines,
        IReadOnlyList<SiteLinkOptionViewModel> sites,
        DatabaseManager databaseManager,
        SessionActivityReporter activity,
        string? preselectedSiteId = null)
    {
        _databaseManager = databaseManager;
        _activity = activity;

        Engines = new ObservableCollection<SqlEngine>(engines);
        Sites = new ObservableCollection<SiteLinkOptionViewModel>(sites);
        SelectedEngine = Engines.FirstOrDefault();
        SelectedSite = Sites.FirstOrDefault(site =>
            preselectedSiteId is not null &&
            string.Equals(site.SiteId, preselectedSiteId, StringComparison.OrdinalIgnoreCase))
            ?? Sites.FirstOrDefault();

        CreateCommand = new RelayCommand(_ => _ = CreateAsync(), _ => CanSubmit());
        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty), _ => !IsBusy);
    }

    public ObservableCollection<SqlEngine> Engines { get; }
    public ObservableCollection<SiteLinkOptionViewModel> Sites { get; }

    public RelayCommand CreateCommand { get; }
    public RelayCommand CloseCommand { get; }

    public event EventHandler<DatabaseRecord>? DatabaseCreated;
    public event EventHandler? RequestClose;

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

    public SqlEngine SelectedEngine
    {
        get => _selectedEngine;
        set => SetProperty(ref _selectedEngine, value);
    }

    public SiteLinkOptionViewModel? SelectedSite
    {
        get => _selectedSite;
        set => SetProperty(ref _selectedSite, value);
    }

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

    public string BusyMessage
    {
        get => _busyMessage;
        private set => SetProperty(ref _busyMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                CreateCommand.RaiseCanExecuteChanged();
                CloseCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public void NotifyCloseBlockedWhileBusy()
    {
        ErrorMessage = string.Empty;
        BusyMessage = "Creating database… please wait or use Cancel after the operation finishes.";
    }

    private bool CanSubmit() => !IsBusy && !string.IsNullOrWhiteSpace(Name);

    private async Task CreateAsync()
    {
        if (IsBusy)
        {
            return;
        }

        ErrorMessage = string.Empty;
        var trimmedName = Name.Trim();
        var engine = SelectedEngine;
        var siteId = SelectedSite?.SiteId;
        var action = $"Create database '{trimmedName}' ({engine})";

        var result = await _activity.RunBackgroundAsync<DatabaseRecord>(
            "Databases",
            action,
            () => Task.FromResult(_databaseManager.Create(trimmedName, engine, siteId)),
            setBusy: value => IsBusy = value,
            successMessage: SessionActivityMessages.DatabaseCreated(trimmedName, engine),
            setStatus: message => BusyMessage = message,
            onError: ex => ErrorMessage = ex.Message,
            busyMessage: "Preparing SQL admin and creating database…",
            failureMessage: $"Failed to create database '{trimmedName}'.").ConfigureAwait(true);

        if (!result.Succeeded || result.Value is null)
        {
            return;
        }

        DatabaseCreated?.Invoke(this, result.Value);
    }
}
