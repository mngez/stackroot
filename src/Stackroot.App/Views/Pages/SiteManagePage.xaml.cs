using System.Windows.Input;
using Stackroot.App.ViewModels;

namespace Stackroot.App.Views.Pages;

public partial class SiteManagePage : System.Windows.Controls.UserControl
{
    public SiteManagePage(SiteManageViewModel viewModel, string siteId)
    {
        viewModel.Load(siteId);
        DataContext = viewModel;
        InitializeComponent();
    }

    private void OnCustomCommandViewLogClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: SiteCustomCommandViewModel cmd })
        {
            return;
        }

        cmd.OpenLog(Keyboard.Modifiers == ModifierKeys.Control);
        e.Handled = true;
    }

    private void OnQuickActionViewLogClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is SiteManageViewModel vm)
        {
            vm.OpenCommandLog(Keyboard.Modifiers == ModifierKeys.Control);
        }

        e.Handled = true;
    }
}
