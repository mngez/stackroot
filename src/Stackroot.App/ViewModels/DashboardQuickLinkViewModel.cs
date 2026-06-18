using System.Diagnostics;
using Stackroot.App.Commands;

namespace Stackroot.App.ViewModels;

public sealed class DashboardQuickLinkViewModel
{
    public required string Label { get; init; }
    public required string Url { get; init; }
    public required RelayCommand OpenCommand { get; init; }
}
