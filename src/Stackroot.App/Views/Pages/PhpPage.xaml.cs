using Stackroot.App.ViewModels;

namespace Stackroot.App.Views.Pages;

public partial class PhpPage : System.Windows.Controls.UserControl
{
    public PhpPage(PhpViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
