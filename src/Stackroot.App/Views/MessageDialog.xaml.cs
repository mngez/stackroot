using System.Windows;
using Stackroot.App.ViewModels;
using Stackroot.App.Windows;

namespace Stackroot.App.Views;

public partial class MessageDialog : Window
{
    public MessageDialog()
    {
        InitializeComponent();
        WindowsTheme.HookDarkTitleBar(this);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowsTheme.ApplyDarkTitleBar(this);
    }

    public static StackrootDialogResult Show(
        Window? owner,
        string title,
        string message,
        StackrootDialogKind kind = StackrootDialogKind.Info,
        StackrootDialogButtons buttons = StackrootDialogButtons.Ok,
        string? details = null,
        string okText = "OK",
        string yesText = "Yes",
        string noText = "No",
        string cancelText = "Cancel")
    {
        var viewModel = new MessageDialogViewModel(
            title,
            message,
            kind,
            buttons,
            details,
            okText,
            yesText,
            noText,
            cancelText);

        var dialog = new MessageDialog
        {
            DataContext = viewModel,
            Owner = owner,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner
        };

        StackrootDialogResult result = StackrootDialogResult.None;
        viewModel.RequestClose += (_, closeResult) =>
        {
            result = closeResult;
            dialog.Close();
        };

        dialog.ShowDialog();
        return result;
    }
}
