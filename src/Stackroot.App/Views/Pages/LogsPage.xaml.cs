using Stackroot.App.ViewModels;

namespace Stackroot.App.Views.Pages;

public partial class LogsPage : System.Windows.Controls.UserControl
{
    public LogsPage(LogsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
