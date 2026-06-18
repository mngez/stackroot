using System.Collections.ObjectModel;
using Stackroot.App.Commands;
using Stackroot.App.Services;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;

namespace Stackroot.App.ViewModels;

public sealed class PackageInstallWithPhpDialogViewModel : ViewModelBase
{
    private string? _selectedPhpPackageId;
    private string _validationMessage = string.Empty;

    public PackageInstallWithPhpDialogViewModel(
        PackageEntry package,
        PackageCatalogStore catalog,
        InstallRegistryStore registry,
        RequiresPhp? requirementFallback = null)
    {
        PackageLabel = package.Label;
        RequirementText = PackagePhpDependencyHelper.FormatRequirement(package, requirementFallback);
        ConfirmCommand = new RelayCommand(_ => Confirm(), _ => CanConfirm);
        CancelCommand = new RelayCommand(_ => Cancel());

        foreach (var option in PackagePhpDependencyHelper.ListCompatiblePhpOptions(package, catalog, registry, requirementFallback))
        {
            PhpOptions.Add(new PhpInstallOptionViewModel(option));
        }

        var preferred = PhpOptions.FirstOrDefault(o => o.IsInstalled)?.Id
            ?? PhpOptions.FirstOrDefault()?.Id;
        SelectedPhpPackageId = preferred;
    }

    public string PackageLabel { get; }
    public string RequirementText { get; }
    public ObservableCollection<PhpInstallOptionViewModel> PhpOptions { get; } = [];
    public bool HasPhpOptions => PhpOptions.Count > 0;
    public bool ShowMissingPhpCatalog => !HasPhpOptions;

    public string? SelectedPhpPackageId
    {
        get => _selectedPhpPackageId;
        set
        {
            if (SetProperty(ref _selectedPhpPackageId, value))
            {
                RaisePropertyChanged(nameof(WillInstallPhp));
                RaisePropertyChanged(nameof(SelectedPhpSummary));
                RaisePropertyChanged(nameof(CanConfirm));
                ConfirmCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set => SetProperty(ref _validationMessage, value);
    }

    public bool WillInstallPhp =>
        !string.IsNullOrWhiteSpace(SelectedPhpPackageId) &&
        PhpOptions.FirstOrDefault(o => string.Equals(o.Id, SelectedPhpPackageId, StringComparison.OrdinalIgnoreCase)) is { IsInstalled: false };

    public string SelectedPhpSummary
    {
        get
        {
            var selected = PhpOptions.FirstOrDefault(o => string.Equals(o.Id, SelectedPhpPackageId, StringComparison.OrdinalIgnoreCase));
            if (selected is null)
            {
                return string.Empty;
            }

            return selected.IsInstalled
                ? $"{selected.Label} is already installed and will be used."
                : $"{selected.Label} will be installed first, then {PackageLabel}.";
        }
    }

    public bool? DialogResult { get; private set; }
    public bool CanConfirm => HasPhpOptions && !string.IsNullOrWhiteSpace(SelectedPhpPackageId);

    public RelayCommand ConfirmCommand { get; }
    public RelayCommand CancelCommand { get; }

    public event EventHandler? RequestClose;

    private void Confirm()
    {
        if (!CanConfirm)
        {
            ValidationMessage = "Select a compatible PHP version.";
            return;
        }

        DialogResult = true;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private void Cancel()
    {
        DialogResult = false;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class PhpInstallOptionViewModel
{
    public PhpInstallOptionViewModel(PhpInstallOption option)
    {
        Id = option.Id;
        Label = option.Label;
        IsInstalled = option.IsInstalled;
    }

    public string Id { get; }
    public string Label { get; }
    public bool IsInstalled { get; }
    public string StatusBadge => IsInstalled ? "Installed" : "Will install";
}
