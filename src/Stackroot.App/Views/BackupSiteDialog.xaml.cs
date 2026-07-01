using System.Windows;
using Stackroot.App.Windows;

namespace Stackroot.App.Views;

public partial class BackupSiteDialog : Window
{
    public BackupSiteDialog()
    {
        InitializeComponent();
        WindowsTheme.HookDarkTitleBar(this);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowsTheme.ApplyDarkTitleBar(this);
    }
}
