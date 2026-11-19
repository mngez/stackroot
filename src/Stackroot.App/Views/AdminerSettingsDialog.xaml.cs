using System.Windows;
using Stackroot.App.Windows;

namespace Stackroot.App.Views;

public partial class AdminerSettingsDialog : Window
{
    public AdminerSettingsDialog()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowsTheme.ApplyDarkTitleBar(this);
        Loaded += (_, _) => WindowsTheme.ApplyDarkTitleBar(this);
    }
}
