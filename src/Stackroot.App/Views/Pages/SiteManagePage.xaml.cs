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

    private void OnCustomCommandRightClick(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && sender is System.Windows.Controls.Button btn && btn.DataContext is SiteCustomCommandViewModel cmd)
        {
            cmd.RemoveCommand.Execute(null);
            e.Handled = true;
        }
    }
}
