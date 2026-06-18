using System.Windows;
using Stackroot.App.ViewModels;
using Stackroot.App.Windows;

namespace Stackroot.App.Views;

public partial class PackageVersionsDialog : Window
{
    public PackageVersionsDialog()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowsTheme.ApplyDarkTitleBar(this);
        Loaded += (_, _) => WindowsTheme.ApplyDarkTitleBar(this);
    }
}
