using System.Collections.ObjectModel;
using System.Windows.Input;

namespace Stackroot.App.ViewModels;

public sealed record NavigationItem(string Key, string Title, string IconGlyph, ICommand Command)
{
    public bool IsFeaturedSite { get; init; }
}
