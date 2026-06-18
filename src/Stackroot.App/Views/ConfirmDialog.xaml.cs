using System.Windows;
using Stackroot.App.ViewModels;
using Stackroot.App.Windows;

namespace Stackroot.App.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
        WindowsTheme.HookDarkTitleBar(this);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowsTheme.ApplyDarkTitleBar(this);
    }

    public static bool Show(
        Window? owner,
        string title,
        string message,
        string confirmText = "Confirm",
        bool isDanger = false)
    {
        return ShowWithCheckbox(owner, title, message, confirmText, isDanger, null).Confirmed;
    }

    public static (bool Confirmed, bool IsChecked) ShowWithCheckbox(
        Window? owner,
        string title,
        string message,
        string confirmText = "Confirm",
        bool isDanger = false,
        string? checkboxLabel = null)
    {
        var viewModel = new ConfirmDialogViewModel(title, message, confirmText, isDanger, checkboxLabel);
        var dialog = new ConfirmDialog
        {
            DataContext = viewModel,
            Owner = owner
        };

        viewModel.RequestClose += (_, result) =>
        {
            dialog.DialogResult = result;
            dialog.Close();
        };

        var confirmed = dialog.ShowDialog() == true;
        return (confirmed, viewModel.IsChecked);
    }
}
