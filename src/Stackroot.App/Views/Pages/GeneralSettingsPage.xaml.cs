using Stackroot.App.ViewModels;

namespace Stackroot.App.Views.Pages;

public partial class GeneralSettingsPage : System.Windows.Controls.UserControl
{
    public GeneralSettingsPage(GeneralSettingsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Unloaded += (_, _) => viewModel.Dispose();
    }
}
