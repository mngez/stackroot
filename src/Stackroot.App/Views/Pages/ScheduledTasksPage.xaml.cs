using Stackroot.App.ViewModels;

namespace Stackroot.App.Views.Pages;

public partial class ScheduledTasksPage : System.Windows.Controls.UserControl
{
    public ScheduledTasksPage(ScheduledTaskViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
