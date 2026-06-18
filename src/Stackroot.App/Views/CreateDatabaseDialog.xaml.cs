using System.ComponentModel;
using System.Windows;
using Stackroot.App.ViewModels;
using Stackroot.App.Windows;

namespace Stackroot.App.Views;

public partial class CreateDatabaseDialog : Window
{
    public CreateDatabaseDialog()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowsTheme.ApplyDarkTitleBar(this);
        Loaded += (_, _) => WindowsTheme.ApplyDarkTitleBar(this);
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (DataContext is not CreateDatabaseDialogViewModel viewModel || !viewModel.IsBusy)
        {
            return;
        }

        e.Cancel = true;
        viewModel.NotifyCloseBlockedWhileBusy();
    }
}
