using System.Windows.Input;
using Stackroot.App.ViewModels;

namespace Stackroot.App.Views.Pages;

public partial class ScheduledTasksPage : System.Windows.Controls.UserControl
{
    public ScheduledTasksPage(ScheduledTaskViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private void OnTaskLogClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: ScheduledTaskRowViewModel row })
        {
            return;
        }

        row.OpenLog(Keyboard.Modifiers == ModifierKeys.Control);
        e.Handled = true;
    }
}
