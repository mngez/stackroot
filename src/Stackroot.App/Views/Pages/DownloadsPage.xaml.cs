using Stackroot.App.ViewModels;

namespace Stackroot.App.Views.Pages;

public partial class DownloadsPage : System.Windows.Controls.UserControl
{
    public DownloadsPage(DownloadsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
