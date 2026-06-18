using Stackroot.App.ViewModels;

namespace Stackroot.App.Views.Pages;

public partial class DatabasesPage : System.Windows.Controls.UserControl
{
    public DatabasesPage(DatabasesViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
