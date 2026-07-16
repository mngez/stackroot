using System.Collections.ObjectModel;

using System.IO;

using System.Windows;

using Stackroot.App.Commands;

using Stackroot.App.Helpers;

using Stackroot.App.Localization;

using Stackroot.App.Services;

using Stackroot.App.Views;

using Stackroot.Core.Databases;

using Stackroot.Core.Databases.Models;



namespace Stackroot.App.ViewModels;



public sealed class DatabaseBackupRowViewModel : ViewModelBase

{

    private bool _isSelected;



    public required DatabaseBackupInfo Info { get; init; }



    public string FileName => Info.FileName;

    public string DatabaseLabel => Info.DatabaseName ?? "Unknown";

    public string EngineLabel => Info.Engine?.ToString() ?? "—";

    public string CreatedLabel => Info.CreatedAt.ToLocalTime().ToString("g");

    public string SizeLabel => FormatSize(Info.SizeBytes);



    public bool IsSelected

    {

        get => _isSelected;

        set => SetProperty(ref _isSelected, value);

    }



    private static string FormatSize(long bytes)

    {

        if (bytes < 1024)

        {

            return $"{bytes} B";

        }



        if (bytes < 1024 * 1024)

        {

            return $"{bytes / 1024.0:0.#} KB";

        }



        return $"{bytes / (1024.0 * 1024.0):0.#} MB";

    }

}



public sealed class DatabaseBackupsDialogViewModel : ViewModelBase

{

    private readonly DatabaseManager _databaseManager;

    private readonly SessionActivityReporter _activity;

    private readonly BackgroundAlertService _alertService;

    private readonly string? _databaseNameFilter;

    private string _statusMessage = string.Empty;

    private string _selectionSummary = string.Empty;

    private bool _isBusy;

    private DatabaseBackupRowViewModel? _selectedBackup;



    public DatabaseBackupsDialogViewModel(

        DatabaseManager databaseManager,

        SessionActivityReporter activity,

        BackgroundAlertService alertService,

        string? databaseNameFilter = null)

    {

        _databaseManager = databaseManager;

        _activity = activity;

        _alertService = alertService;

        _databaseNameFilter = databaseNameFilter;



        Backups = [];

        RestoreCommand = new RelayCommand(_ => _ = RestoreSelectedAsync(), _ => CanRestore);

        OpenFolderCommand = new RelayCommand(_ => OpenBackupFolder());

        RefreshCommand = new RelayCommand(_ => Reload(), _ => !IsBusy);

        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty));

        DeleteBackupCommand = new RelayCommand(row => DeleteBackup(row as DatabaseBackupRowViewModel), _ => !IsBusy);



        Reload();

    }



    public ObservableCollection<DatabaseBackupRowViewModel> Backups { get; }



    public string Title => string.IsNullOrWhiteSpace(_databaseNameFilter)

        ? "Database backups"

        : $"Backups for \"{_databaseNameFilter}\"";



    public string StatusMessage

    {

        get => _statusMessage;

        private set => SetProperty(ref _statusMessage, value);

    }



    public string SelectionSummary

    {

        get => _selectionSummary;

        private set => SetProperty(ref _selectionSummary, value);

    }



    public bool IsBusy

    {

        get => _isBusy;

        private set

        {

            if (SetProperty(ref _isBusy, value))

            {

                RestoreCommand.RaiseCanExecuteChanged();

                RefreshCommand.RaiseCanExecuteChanged();

            }

        }

    }



    public DatabaseBackupRowViewModel? SelectedBackup

    {

        get => _selectedBackup;

        set

        {

            if (_selectedBackup is not null)

            {

                _selectedBackup.IsSelected = false;

            }



            if (!SetProperty(ref _selectedBackup, value))

            {

                return;

            }



            if (_selectedBackup is not null)

            {

                _selectedBackup.IsSelected = true;

            }



            RestoreCommand.RaiseCanExecuteChanged();

            UpdateSelectionSummary();

        }

    }



    public bool HasBackups => Backups.Count > 0;

    public bool CanRestore => !IsBusy && SelectedBackup is not null;



    public RelayCommand RestoreCommand { get; }

    public RelayCommand DeleteBackupCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand CloseCommand { get; }



    public event EventHandler? RequestClose;



    public void Reload()

    {

        Backups.Clear();

        foreach (var backup in _databaseManager.ListBackups(_databaseNameFilter))

        {

            Backups.Add(new DatabaseBackupRowViewModel { Info = backup });

        }



        SelectedBackup = Backups.FirstOrDefault();

        RaisePropertyChanged(nameof(HasBackups));

        StatusMessage = HasBackups

            ? $"{Backups.Count} backup file(s) found."

            : "No backup files found yet.";

    }



    private void UpdateSelectionSummary()

    {

        if (SelectedBackup is null)

        {

            SelectionSummary = "No backup selected.";

            return;

        }



        SelectionSummary =

            $"Selected: {SelectedBackup.FileName} · {SelectedBackup.DatabaseLabel} · {SelectedBackup.EngineLabel} · {SelectedBackup.CreatedLabel} · {SelectedBackup.SizeLabel}";

    }



    private async Task RestoreSelectedAsync()

    {

        if (SelectedBackup is null)

        {

            return;

        }



        var backup = SelectedBackup.Info;

        var choice = PickRestoreTarget(backup.DatabaseName ?? _databaseNameFilter);
        if (choice is null)
        {
            return;
        }

        var targetName = choice.DatabaseName;

        if (string.IsNullOrWhiteSpace(targetName))
        {
            StatusMessage = "Could not determine the target database name.";
            return;
        }

        var result = await _activity.RunBackgroundAsync<string>(
            "Databases",
            $"Restore database '{targetName}'",
            () => Task.Run(() => _databaseManager.RestoreBackup(
                backup.FullPath,
                targetName,
                choice.ReplaceExistingDatabase,
                choice.DisableForeignKeyChecks)),

            value => IsBusy = value,

            successMessage: SessionActivityMessages.DatabaseBackupRestored(targetName, backup.FileName),

            setStatus: message => StatusMessage = message,

            onError: ex => StatusMessage = ex.Message,

            busyMessage: $"Restoring \"{targetName}\" from {backup.FileName}…",

            failureMessage: $"Restore failed for \"{targetName}\".").ConfigureAwait(true);



        if (result.Succeeded)

        {

            Reload();
            StatusMessage = $"Restored \"{targetName}\" from {backup.FileName}.";
            _alertService.Raise(new BackgroundAlert(
                BackgroundAlertKind.Success,
                LocalizationManager.Get("Loc.BackgroundAlert.DbRestoreComplete", "Restore Complete"),
                string.Format(LocalizationManager.Get("Loc.BackgroundAlert.DbRestoreComplete.Body", "Database \"{0}\" restored from {1}."), targetName, backup.FileName)));
        }
        else
        {
            _alertService.Raise(new BackgroundAlert(
                BackgroundAlertKind.Error,
                LocalizationManager.Get("Loc.BackgroundAlert.DbRestoreFailed", "Restore Failed"),
                string.Format(LocalizationManager.Get("Loc.BackgroundAlert.DbRestoreFailed.Body", "Could not restore database \"{0}\"."), targetName)));
        }

    }



    private void OpenBackupFolder()

    {

        var folder = Backups.FirstOrDefault()?.Info.FullPath;

        folder = folder is null ? null : Path.GetDirectoryName(folder);

        if (string.IsNullOrWhiteSpace(folder))

        {

            return;

        }



        Directory.CreateDirectory(folder);

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo

        {

            FileName = folder,

            UseShellExecute = true

        });

    }

    private void DeleteBackup(DatabaseBackupRowViewModel? row)
    {
        if (row is null) return;

        if (!ConfirmDialog.Show(
                Application.Current?.MainWindow,
                "Delete backup?",
                $"Delete backup file \"{row.FileName}\"? This cannot be undone.",
                "Delete",
                isDanger: true))
        {
            return;
        }

        try { File.Delete(row.Info.FullPath); } catch { }

        Backups.Remove(row);
        if (SelectedBackup == row)
            SelectedBackup = Backups.FirstOrDefault();

        RaisePropertyChanged(nameof(HasBackups));
        StatusMessage = HasBackups
            ? $"{Backups.Count} backup file(s) found."
            : "No backup files found yet.";
    }

    private RestoreTargetChoice? PickRestoreTarget(string? suggestedName)
    {
        var databases = _databaseManager.List();
        if (databases.Count == 0)
        {
            StatusMessage = "No databases exist to restore into. Create a database first.";
            return null;
        }

        var options = databases.Select(db => db.Name).ToList();
        var initial = !string.IsNullOrWhiteSpace(_databaseNameFilter)
            ? _databaseNameFilter
            : suggestedName is not null && options.Contains(suggestedName, StringComparer.OrdinalIgnoreCase)
                ? suggestedName
                : options[0];

        var dialogVm = new PickDatabaseForRestoreDialogViewModel(options, initial);
        var owner = System.Windows.Application.Current?.MainWindow;
        var dialog = new Views.PickDatabaseForRestoreDialog
        {
            DataContext = dialogVm,
            Owner = owner
        };

        dialogVm.RequestClose += (_, _) => dialog.Close();
        dialog.ShowDialog();

        if (!dialogVm.IsConfirmed || string.IsNullOrWhiteSpace(dialogVm.SelectedDatabase))
        {
            return null;
        }

        return new RestoreTargetChoice(
            dialogVm.SelectedDatabase,
            dialogVm.ReplaceDatabaseBeforeRestore,
            dialogVm.DisableForeignKeyChecks);
    }

    private sealed record RestoreTargetChoice(
        string DatabaseName,
        bool ReplaceExistingDatabase,
        bool DisableForeignKeyChecks);
}

public sealed class PickDatabaseForRestoreDialogViewModel : ViewModelBase
{
    public ObservableCollection<string> Databases { get; } = [];

    private string? _selectedDatabase;
    private bool _replaceDatabaseBeforeRestore;
    private bool _disableForeignKeyChecks;

    public string? SelectedDatabase
    {
        get => _selectedDatabase;
        set => SetProperty(ref _selectedDatabase, value);
    }

    public bool ReplaceDatabaseBeforeRestore
    {
        get => _replaceDatabaseBeforeRestore;
        set => SetProperty(ref _replaceDatabaseBeforeRestore, value);
    }

    public bool DisableForeignKeyChecks
    {
        get => _disableForeignKeyChecks;
        set => SetProperty(ref _disableForeignKeyChecks, value);
    }

    public RelayCommand RestoreCommand { get; }
    public RelayCommand CancelCommand { get; }

    public bool IsConfirmed { get; private set; }

    public event EventHandler? RequestClose;

    public PickDatabaseForRestoreDialogViewModel(List<string> databaseNames, string? initialSelection)
    {
        foreach (var name in databaseNames)
            Databases.Add(name);

        SelectedDatabase = initialSelection;

        RestoreCommand = new RelayCommand(_ => ConfirmRestore(), _ => !string.IsNullOrWhiteSpace(SelectedDatabase));
        CancelCommand = new RelayCommand(_ => CancelRestore());
    }

    public void CancelRestore()
    {
        AbortRestore();
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Marks the dialog as cancelled without requesting another close (e.g. when the window X is clicked).</summary>
    public void AbortRestore()
    {
        IsConfirmed = false;
        SelectedDatabase = null;
    }

    private void ConfirmRestore()
    {
        if (string.IsNullOrWhiteSpace(SelectedDatabase))
        {
            return;
        }

        IsConfirmed = true;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }
}


