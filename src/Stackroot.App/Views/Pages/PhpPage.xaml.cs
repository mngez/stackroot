using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Stackroot.App.ViewModels;

namespace Stackroot.App.Views.Pages;

public partial class PhpPage : UserControl
{
    public PhpPage(PhpViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => viewModel.BeginLoading();
        Unloaded += (_, _) => viewModel.EndLoading();
    }

    private void OpenRowMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button
            || button.Tag is not PhpVersionRowViewModel row)
        {
            return;
        }

        // PlacementTarget is cleared when the menu closes, so keep the row on Tag.
        menu.Tag = row;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;

        if (DataContext is not PhpViewModel viewModel)
        {
            return;
        }

        var canRun = row.CanInteract && !viewModel.IsBusy;
        foreach (var child in menu.Items)
        {
            if (child is not Button menuButton)
            {
                continue;
            }

            if (string.Equals(menuButton.Tag as string, "uninstall", StringComparison.OrdinalIgnoreCase))
            {
                menuButton.Content = row.UninstallButtonLabel;
                menuButton.IsEnabled = canRun && viewModel.UninstallCommand.CanExecute(row.Id);
            }
            else
            {
                menuButton.IsEnabled = canRun;
            }
        }

        menu.IsOpen = true;
        e.Handled = true;
    }

    private void ExportProfileMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetMenuRow(sender, out var viewModel, out var row))
        {
            return;
        }

        viewModel.ExportProfileForVersion(row.Id);
        CloseParentMenu(sender);
        e.Handled = true;
    }

    private void ImportProfileMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetMenuRow(sender, out var viewModel, out var row))
        {
            return;
        }

        viewModel.ImportProfileForVersion(row.Id);
        CloseParentMenu(sender);
        e.Handled = true;
    }

    private void UninstallMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetMenuRow(sender, out var viewModel, out var row))
        {
            return;
        }

        if (viewModel.UninstallCommand.CanExecute(row.Id))
        {
            viewModel.UninstallCommand.Execute(row.Id);
        }

        CloseParentMenu(sender);
        e.Handled = true;
    }

    private static void CloseParentMenu(object sender)
    {
        if (sender is Button { Parent: ContextMenu menu })
        {
            menu.IsOpen = false;
        }
    }

    private bool TryGetMenuRow(object sender, out PhpViewModel viewModel, out PhpVersionRowViewModel row)
    {
        row = null!;

        if (DataContext is not PhpViewModel vm)
        {
            viewModel = null!;
            return false;
        }

        viewModel = vm;

        if (sender is not Button { Parent: ContextMenu { Tag: PhpVersionRowViewModel taggedRow } })
        {
            return false;
        }

        row = taggedRow;
        return row.CanInteract && !viewModel.IsBusy && !string.IsNullOrWhiteSpace(row.Id);
    }
}
