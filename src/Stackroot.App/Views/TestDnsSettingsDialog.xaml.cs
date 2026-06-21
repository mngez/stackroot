using System.Windows;
using Stackroot.App.Windows;

namespace Stackroot.App.Views;

public partial class TestDnsSettingsDialog : Window
{
    public TestDnsSettingsDialog()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowsTheme.ApplyDarkTitleBar(this);
        Loaded += (_, _) => WindowsTheme.ApplyDarkTitleBar(this);
    }
}
