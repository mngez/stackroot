using Stackroot.App.ViewModels;

namespace Stackroot.App.Views.Pages;

public partial class PerformancePage : System.Windows.Controls.UserControl
{
    public PerformancePage(PerformanceViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => viewModel.BeginLoading();
        Unloaded += (_, _) => viewModel.EndLoading();
    }
}
