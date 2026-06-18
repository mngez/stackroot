using System.Windows;
using Stackroot.App.ViewModels;

namespace Stackroot.App.Views;

public partial class LaravelInstallDialog : Window
{
    private LaravelInstallResult? _result;

    private LaravelInstallDialog(string siteName, string siteDomain)
    {
        InitializeComponent();

        var vm = new LaravelInstallDialogViewModel(siteName, siteDomain);
        vm.RequestClose += (_, result) =>
        {
            _result = result;
            DialogResult = result is not null;
            Close();
        };

        DataContext = vm;
    }

    public static LaravelInstallResult? Show(Window? owner, string siteName, string siteDomain)
    {
        var dialog = new LaravelInstallDialog(siteName, siteDomain) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog._result : null;
    }
}
