using System.Windows.Controls;

using System.Windows.Input;

using Stackroot.App.ViewModels;



namespace Stackroot.App.Views;



public partial class DownloadTray : UserControl

{

    public DownloadTray()

    {

        InitializeComponent();

        TrayPopup.Closed += (_, _) =>

        {

            if (DataContext is DownloadTrayViewModel vm && vm.IsOpen)

            {

                vm.IsOpen = false;

            }

        };

    }



    private void TrayButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)

    {

        if (DataContext is not DownloadTrayViewModel vm)

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


