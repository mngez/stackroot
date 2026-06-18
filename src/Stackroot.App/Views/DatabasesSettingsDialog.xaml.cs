using System.Windows;
using Stackroot.App.Windows;

namespace Stackroot.App.Views;

public partial class DatabasesSettingsDialog : Window
{
    public DatabasesSettingsDialog()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowsTheme.ApplyDarkTitleBar(this);
        Loaded += (_, _) => WindowsTheme.ApplyDarkTitleBar(this);
    }
}
