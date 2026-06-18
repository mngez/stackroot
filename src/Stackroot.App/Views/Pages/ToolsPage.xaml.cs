using Stackroot.App.ViewModels;

namespace Stackroot.App.Views.Pages;

public partial class ToolsPage : System.Windows.Controls.UserControl
{
    public ToolsPage(ToolsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
