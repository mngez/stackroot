using Stackroot.App.ViewModels;

namespace Stackroot.App.Views.Pages;

public partial class NodePage : System.Windows.Controls.UserControl
{
    public NodePage(NodeViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
