using System.Windows;

using Stackroot.App.Windows;



namespace Stackroot.App.Views;



public partial class MailpitSettingsDialog : Window

{

    public MailpitSettingsDialog()

    {

        InitializeComponent();

        SourceInitialized += (_, _) => WindowsTheme.ApplyDarkTitleBar(this);

        Loaded += (_, _) => WindowsTheme.ApplyDarkTitleBar(this);

    }

}

