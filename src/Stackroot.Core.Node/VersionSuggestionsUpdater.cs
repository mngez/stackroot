using System.Collections.ObjectModel;

namespace Stackroot.Core.Node;

public static class VersionSuggestionsUpdater
{
    public static bool CanInstall(bool isBusy, bool nvmInstalled, string? versionInput)
        => !isBusy && nvmInstalled && !string.IsNullOrWhiteSpace(versionInput);

    public static bool CanActivate(bool isBusy, bool nvmInstalled, string? selectedVersion)
        => !isBusy && nvmInstalled && !string.IsNullOrWhiteSpace(selectedVersion);

    public static void Apply(ObservableCollection<string> target, IReadOnlyList<string> suggestions)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(suggestions);

        for (var index = target.Count - 1; index >= 0; index--)
        {
            var existing = target[index];
            if (!suggestions.Any(item => string.Equals(item, existing, StringComparison.OrdinalIgnoreCase)))
            {
                target.RemoveAt(index);
            }
        }

        for (var index = 0; index < suggestions.Count; index++)
        {
            var suggestion = suggestions[index];
            var existingIndex = FindIndex(target, suggestion);
            if (existingIndex < 0)
            {
                if (index >= target.Count)
                {
                    target.Add(suggestion);
                }
                else
                {
                    target.Insert(index, suggestion);
                }

                continue;
            }

            if (existingIndex != index)
            {
                target.Move(existingIndex, index);
            }
        }
    }

    public static string? ResolveInstalledSelection(
        string? previousSelection,
        IReadOnlyList<string> installedVersions,
        string? activeVersion)
    {
        if (!string.IsNullOrWhiteSpace(previousSelection)
            && installedVersions.Any(version => string.Equals(version, previousSelection, StringComparison.OrdinalIgnoreCase)))
        {
            return installedVersions.First(version =>
                string.Equals(version, previousSelection, StringComparison.OrdinalIgnoreCase));
        }

        if (string.IsNullOrWhiteSpace(activeVersion))
        {
            return null;
        }

        return installedVersions.FirstOrDefault(version =>
            string.Equals(version, activeVersion, StringComparison.OrdinalIgnoreCase));
    }

    private static int FindIndex(ObservableCollection<string> target, string value)
    {
        for (var index = 0; index < target.Count; index++)
        {
            if (string.Equals(target[index], value, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }
}
