using System.Windows.Controls;
using System.Windows.Input;
using Stackroot.App.ViewModels;

namespace Stackroot.App.Views;

public partial class SessionActivityTray : UserControl
{
    public SessionActivityTray()
    {
        InitializeComponent();
        TrayPopup.Closed += (_, _) =>
        {
            if (DataContext is SessionActivityTrayViewModel vm && vm.IsOpen)
            {
                vm.IsOpen = false;
            }
        };
    }

    private void TrayButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not SessionActivityTrayViewModel vm)
        {
            return;
        }

        if (vm.IsOpen)
        {
            vm.IsOpen = false;
            e.Handled = true;
        }
    }
}
