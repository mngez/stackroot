using System.Windows;
using System.Windows.Controls;
using Stackroot.App.ViewModels;
using Stackroot.App.Windows;

namespace Stackroot.App.Views;

public partial class PhpExtensionsDialog : Window
{
    public PhpExtensionsDialog()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowsTheme.ApplyDarkTitleBar(this);
        Loaded += (_, _) => WindowsTheme.ApplyDarkTitleBar(this);
    }

    private void ExtensionToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox || checkBox.DataContext is not PhpExtensionRowViewModel row)
        {
            return;
        }

        if (DataContext is not PhpExtensionsDialogViewModel viewModel)
        {
            return;
        }

        checkBox.IsChecked = row.Enabled;
        if (viewModel.ToggleExtensionCommand.CanExecute(row))
        {
            viewModel.ToggleExtensionCommand.Execute(row);
        }
    }
}
