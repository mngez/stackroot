using Stackroot.App.ViewModels;

namespace Stackroot.App.Views.Pages;

public partial class DashboardPage : System.Windows.Controls.UserControl
{
    public DashboardPage(DashboardViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => viewModel.BeginLoading();
        Unloaded += (_, _) => viewModel.EndLoading();
    }
}
