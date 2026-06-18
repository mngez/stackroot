using System.Windows;
using Stackroot.App.ViewModels;

namespace Stackroot.App.Views;

public partial class WordPressInstallDialog : Window
{
    public WordPressInstallDialog()
    {
        InitializeComponent();
    }

    public static WordPressInstallInput? Show(Window? owner, string siteTitle, string domain)
    {
        var vm = new WordPressInstallDialogViewModel(siteTitle, domain);
        var dialog = new WordPressInstallDialog
        {
            DataContext = vm,
            Owner = owner
        };

        vm.RequestClose += (_, _) =>
        {
            dialog.DialogResult = vm.Result is not null;
            dialog.Close();
        };

        return dialog.ShowDialog() == true ? vm.Result : null;
    }
}
