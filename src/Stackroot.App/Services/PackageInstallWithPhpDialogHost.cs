using System.Windows;
using Stackroot.App.ViewModels;
using Stackroot.App.Views;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;

namespace Stackroot.App.Services;

public static class PackageInstallWithPhpDialogHost
{
    public static bool TryPrompt(
        Window? owner,
        PackageEntry package,
        PackageCatalogStore catalog,
        InstallRegistryStore registry,
        RequiresPhp? requirementFallback,
        out string? selectedPhpPackageId)
    {
        selectedPhpPackageId = null;
        if (package.RequiresPhp is null && requirementFallback is null)
        {
            return true;
        }

        var dialogVm = new PackageInstallWithPhpDialogViewModel(package, catalog, registry, requirementFallback);
        var dialog = new PackageInstallWithPhpDialog
        {
            DataContext = dialogVm,
            Owner = owner
        };

        dialogVm.RequestClose += (_, _) => dialog.Close();
        dialog.ShowDialog();

        if (dialogVm.DialogResult != true || string.IsNullOrWhiteSpace(dialogVm.SelectedPhpPackageId))
        {
            return false;
        }

        selectedPhpPackageId = dialogVm.SelectedPhpPackageId;
        return true;
    }

    public static async Task EnsurePhpInstalledAsync(
        string phpPackageId,
        PackageCatalogStore catalog,
        InstallRegistryStore registry,
        PackageInstallCoordinator packages,
        InstallProgressCallback? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        if (registry.IsInstalled(phpPackageId))
        {
            return;
        }

        var phpPackage = catalog.GetById(phpPackageId)
            ?? throw new InvalidOperationException($"Catalog package '{phpPackageId}' was not found.");

        await packages.InstallAsync(phpPackage, onProgress, cancellationToken);
    }
}
