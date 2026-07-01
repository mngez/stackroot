using Stackroot.App.Commands;

namespace Stackroot.App.ViewModels;

public sealed class DeleteSiteDialogViewModel : ViewModelBase
{
    public DeleteSiteDialogViewModel(
        string title,
        string message,
        bool hasDatabases,
        bool hasScheduledTasks,
        bool hasProcesses)
    {
        Title = title;
        Message = message;
        HasDatabases = hasDatabases;
        HasScheduledTasks = hasScheduledTasks;
        HasProcesses = hasProcesses;

        ConfirmCommand = new RelayCommand(_ => RequestClose?.Invoke(this, true));
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke(this, false));
    }

    public string Title { get; }
    public string Message { get; }
    public bool HasDatabases { get; }
    public bool HasScheduledTasks { get; }
    public bool HasProcesses { get; }

    private bool _deleteFiles;
    public bool DeleteFiles
    {
        get => _deleteFiles;
        set => SetProperty(ref _deleteFiles, value);
    }

    private bool _deleteDatabases;
    public bool DeleteDatabases
    {
        get => _deleteDatabases;
        set => SetProperty(ref _deleteDatabases, value);
    }

    private bool _deleteScheduledTasks;
    public bool DeleteScheduledTasks
    {
        get => _deleteScheduledTasks;
        set => SetProperty(ref _deleteScheduledTasks, value);
    }

    private bool _deleteProcesses;
    public bool DeleteProcesses
    {
        get => _deleteProcesses;
        set => SetProperty(ref _deleteProcesses, value);
    }

    public RelayCommand ConfirmCommand { get; }
    public RelayCommand CancelCommand { get; }

    public event EventHandler<bool>? RequestClose;
}
