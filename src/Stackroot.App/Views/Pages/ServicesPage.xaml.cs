using Stackroot.App.ViewModels;

namespace Stackroot.App.Views.Pages;

public partial class ServicesPage : System.Windows.Controls.UserControl
{
    public ServicesPage(ServicesViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => viewModel.BeginLoading();
    }
}
