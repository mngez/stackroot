using Stackroot.App.ViewModels;

namespace Stackroot.App.Views.Pages;

public partial class SitesPage : System.Windows.Controls.UserControl
{
    public SitesPage(SitesViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
