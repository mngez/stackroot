using System.Windows;
using Stackroot.App.ViewModels;
using Stackroot.App.Windows;

namespace Stackroot.App.Views;

public partial class DeleteSiteDialog : Window
{
    public DeleteSiteDialog()
    {
        InitializeComponent();
        WindowsTheme.HookDarkTitleBar(this);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowsTheme.ApplyDarkTitleBar(this);
    }

    public static DeleteSiteDialogResult Show(
        Window? owner,
        string title,
        string message,
        bool hasDatabases,
        bool hasScheduledTasks,
        bool hasProcesses)
    {
        var viewModel = new DeleteSiteDialogViewModel(title, message, hasDatabases, hasScheduledTasks, hasProcesses);
        var dialog = new DeleteSiteDialog
        {
            DataContext = viewModel,
            Owner = owner
        };

        viewModel.RequestClose += (_, confirmed) =>
        {
            dialog.DialogResult = confirmed;
            dialog.Close();
        };

        var confirmed = dialog.ShowDialog() == true;
        return new DeleteSiteDialogResult(
            confirmed,
            viewModel.DeleteFiles,
            viewModel.DeleteDatabases,
            viewModel.DeleteScheduledTasks,
            viewModel.DeleteProcesses);
    }
}

public sealed record DeleteSiteDialogResult(
    bool Confirmed,
    bool DeleteFiles,
    bool DeleteDatabases,
    bool DeleteScheduledTasks,
    bool DeleteProcesses);
