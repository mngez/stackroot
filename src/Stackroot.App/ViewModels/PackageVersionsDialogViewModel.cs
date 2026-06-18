using System.Collections.ObjectModel;
using Stackroot.App.Commands;
using Stackroot.App.Services;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Catalog;

namespace Stackroot.App.ViewModels;

public sealed class PackageVersionsDialogViewModel : ViewModelBase
{
    private readonly Dictionary<string, PackageEntry> _packagesById;
    private readonly InstallRegistryStore _registry;
    private readonly Func<string?> _resolveActivePackageId;
    private readonly Func<PackageEntry, Task> _install;
    private readonly Func<PackageEntry, Task> _uninstall;
    private readonly Func<PackageEntry, Task>? _activate;
    private PackageVersionOptionViewModel? _selectedVersion;

    public PackageVersionsDialogViewModel(
        string title,
        string hint,
        IReadOnlyList<PackageEntry> catalog,
        InstallRegistryStore registry,
        Func<string?> resolveActivePackageId,
        Func<PackageEntry, Task> install,
        Func<PackageEntry, Task> uninstall,
        Func<PackageEntry, Task>? activate = null)
    {
        Title = title;
        Hint = hint;
        _registry = registry;
        _resolveActivePackageId = resolveActivePackageId;
        _install = install;
        _uninstall = uninstall;
        _activate = activate;
        _packagesById = catalog.ToDictionary(package => package.Id, StringComparer.OrdinalIgnoreCase);

        CloseCommand = new RelayCommand(_ => RequestClose?.Invoke(this, EventArgs.Empty));
        PrimaryActionCommand = new RelayCommand(
            _ => ExecutePrimaryAction(),
            _ => CanExecutePrimaryAction);

        Versions = new ObservableCollection<PackageVersionOptionViewModel>(
            catalog.Select(CreateOption));
        SelectDefaultVersion();
    }

    public string Title { get; }
    public string Hint { get; }
    public ObservableCollection<PackageVersionOptionViewModel> Versions { get; }
    public bool HasVersions => Versions.Count > 0;
    public bool ShowEmptyCatalog => !HasVersions;
    public RelayCommand CloseCommand { get; }
    public RelayCommand PrimaryActionCommand { get; }

    public PackageVersionOptionViewModel? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (SetProperty(ref _selectedVersion, value))
            {
                RaisePrimaryActionChanged();
            }
        }
    }

    public string PrimaryActionLabel => SelectedVersion switch
    {
        { IsActive: true } => "Uninstall",
        { IsInstalled: true } when _activate is not null => "Use this version",
        { IsInstalled: false } => "Install",
        _ => "Install"
    };

    public bool IsPrimaryActionDanger => SelectedVersion?.IsActive == true;

    public bool CanExecutePrimaryAction => SelectedVersion switch
    {
        null => false,
        { IsActive: true } => true,
        { IsInstalled: true } => _activate is not null,
        _ => true
    };

    public event EventHandler? RequestClose;

    private void SelectDefaultVersion()
    {
        var activeId = _resolveActivePackageId();
        SelectedVersion = Versions.FirstOrDefault(version =>
                           !string.IsNullOrWhiteSpace(activeId)
                           && string.Equals(version.PackageId, activeId, StringComparison.OrdinalIgnoreCase))
                       ?? Versions.FirstOrDefault();
    }

    private PackageVersionOptionViewModel CreateOption(PackageEntry package)
    {
        var activeId = _resolveActivePackageId();
        var installed = _registry.IsInstalled(package.Id);
        var active = installed
                     && !string.IsNullOrWhiteSpace(activeId)
                     && string.Equals(package.Id, activeId, StringComparison.OrdinalIgnoreCase);

        return new PackageVersionOptionViewModel
        {
            PackageId = package.Id,
            Label = package.Label,
            Description = package.Description ?? string.Empty,
            IsInstalled = installed,
            IsActive = active
        };
    }

    private void ExecutePrimaryAction()
    {
        var selected = SelectedVersion;
        if (selected is null || !CanExecutePrimaryAction)
        {
            return;
        }

        if (!_packagesById.TryGetValue(selected.PackageId, out var package))
        {
            return;
        }

        RequestClose?.Invoke(this, EventArgs.Empty);
        _ = RunPrimaryActionInBackgroundAsync(selected, package);
    }

    private async Task RunPrimaryActionInBackgroundAsync(
        PackageVersionOptionViewModel selected,
        PackageEntry package)
    {
        try
        {
            if (selected.IsActive)
            {
                await _uninstall(package).ConfigureAwait(false);
                return;
            }

            if (selected.IsInstalled)
            {
                if (_activate is not null)
                {
                    await _activate(package).ConfigureAwait(false);
                }

                return;
            }

            await _install(package).ConfigureAwait(false);
        }
        catch
        {
            // Parent install/uninstall handlers surface errors on the page or tray.
        }
    }

    private void RaisePrimaryActionChanged()
    {
        RaisePropertyChanged(nameof(PrimaryActionLabel));
        RaisePropertyChanged(nameof(IsPrimaryActionDanger));
        RaisePropertyChanged(nameof(CanExecutePrimaryAction));
        PrimaryActionCommand.RaiseCanExecuteChanged();
    }
}

public sealed class PackageVersionOptionViewModel : ViewModelBase
{
    public required string PackageId { get; init; }
    public required string Label { get; init; }
    public required string Description { get; init; }
    public required bool IsInstalled { get; init; }
    public required bool IsActive { get; init; }

    public bool ShowDescription => !string.IsNullOrWhiteSpace(Description);
    public bool ShowInstalledBadge => IsInstalled && !IsActive;
    public bool ShowActiveBadge => IsActive;
}
