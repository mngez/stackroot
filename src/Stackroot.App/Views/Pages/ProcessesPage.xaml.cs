using Stackroot.App.ViewModels;

namespace Stackroot.App.Views.Pages;

public partial class ProcessesPage : System.Windows.Controls.UserControl
{
    public ProcessesPage(ProcessesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => viewModel.BeginLoading();
        Unloaded += (_, _) =>
        {
            viewModel.EndLoading();
            viewModel.Dispose();
        };
    }
}
