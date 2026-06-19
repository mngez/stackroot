using System.ComponentModel;
using System.Windows;
using Stackroot.App.ViewModels;
using Stackroot.App.Windows;

namespace Stackroot.App.Views;

public partial class PickDatabaseForRestoreDialog : Window
{
    public PickDatabaseForRestoreDialog()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowsTheme.ApplyDarkTitleBar(this);
        Loaded += (_, _) => WindowsTheme.ApplyDarkTitleBar(this);
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (DataContext is PickDatabaseForRestoreDialogViewModel viewModel && !viewModel.IsConfirmed)
        {
            viewModel.AbortRestore();
        }
    }
}
