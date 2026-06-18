using System.Windows;
using Stackroot.App.Windows;

namespace Stackroot.App.Views;

public partial class NodeSettingsDialog : Window
{
    public NodeSettingsDialog()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowsTheme.ApplyDarkTitleBar(this);
        Loaded += (_, _) => WindowsTheme.ApplyDarkTitleBar(this);
    }
}
