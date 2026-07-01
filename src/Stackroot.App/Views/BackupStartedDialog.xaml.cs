using System.Windows;
using Stackroot.App.Windows;

namespace Stackroot.App.Views;

public partial class BackupStartedDialog : Window
{
    public BackupStartedDialog(string siteDomain)
    {
        InitializeComponent();
        WindowsTheme.HookDarkTitleBar(this);
        MessageText.Text = $"{siteDomain} is being backed up in the background.\nYou will be notified when it completes.";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowsTheme.ApplyDarkTitleBar(this);
    }

    public static void Show(Window? owner, string siteDomain)
    {
        var dialog = new BackupStartedDialog(siteDomain) { Owner = owner };
        dialog.ShowDialog();
    }

    private void OnOk(object sender, RoutedEventArgs e) => Close();
}
