namespace Stackroot.Core.Sites.Models;

public static class SitePhpSelection
{
    public const string NoneId = "no-php";

    public static string? ToStoredId(string? selectedId)
    {
        if (string.IsNullOrWhiteSpace(selectedId))
        {
            return null;
        }

        var trimmed = selectedId.Trim();
        return string.Equals(trimmed, NoneId, StringComparison.OrdinalIgnoreCase) ? null : trimmed;
    }
}
