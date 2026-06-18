using System.Collections.ObjectModel;

namespace Stackroot.App.ViewModels;

public sealed class ServiceGroupViewModel
{
    public required string Title { get; init; }
    public ObservableCollection<ServiceEntryViewModel> Items { get; } = [];
}
