using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Stackroot.App.ViewModels;

namespace Stackroot.App.Views.Controls;

public partial class SiteCustomCommandsToolbar : UserControl
{
    public static readonly DependencyProperty ShowOpenSiteButtonProperty =
        DependencyProperty.Register(
            nameof(ShowOpenSiteButton),
            typeof(bool),
            typeof(SiteCustomCommandsToolbar),
            new PropertyMetadata(false));

    public static readonly DependencyProperty ShowOpenAdminButtonProperty =
        DependencyProperty.Register(
            nameof(ShowOpenAdminButton),
            typeof(bool),
            typeof(SiteCustomCommandsToolbar),
            new PropertyMetadata(false));

    public SiteCustomCommandsToolbar()
    {
        InitializeComponent();
    }

    public bool ShowOpenSiteButton
    {
        get => (bool)GetValue(ShowOpenSiteButtonProperty);
        set => SetValue(ShowOpenSiteButtonProperty, value);
    }

    public bool ShowOpenAdminButton
    {
        get => (bool)GetValue(ShowOpenAdminButtonProperty);
        set => SetValue(ShowOpenAdminButtonProperty, value);
    }

    private void OnCustomCommandViewLogClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button { DataContext: SiteCustomCommandViewModel cmd })
        {
            return;
        }

        cmd.OpenLog(Keyboard.Modifiers == ModifierKeys.Control);
        e.Handled = true;
    }
}
