using System.Collections.ObjectModel;

namespace Stackroot.App.ViewModels;

public sealed class ToolGroupViewModel
{
    public required string Title { get; init; }
    public ObservableCollection<ViewModelBase> Items { get; } = [];
}
