namespace Stackroot.Core.Abstractions.DataDocuments;

public static class SettingsDocumentRules
{
    /// <summary>Service keys removed from the app model but may still exist in older settings files.</summary>
    public static readonly HashSet<string> ObsoleteServiceKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "apache"
    };
}
