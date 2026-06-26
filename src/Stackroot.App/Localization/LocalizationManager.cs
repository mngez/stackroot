using System.Windows;

namespace Stackroot.App.Localization;

public static class LocalizationManager
{
    private static readonly HashSet<string> RtlCodes = ["ar", "he", "fa", "ur"];

    public static string CurrentLanguage { get; private set; } = "en";

    public static bool IsRtl => RtlCodes.Contains(CurrentLanguage);

    public static event EventHandler? LanguageChanged;

public static IReadOnlyList<LanguageOption> AvailableLanguages { get; } = [
        new("en", "English"),    
        new("de", "Deutsch"),
        new("es", "Español"),
        new("fr", "Français"),
        new("it", "Italiano"),
        new("ja", "日本語"),
        new("ko", "한국어"),
        new("nl", "Nederlands"),
        new("pl", "Polski"),
        new("pt", "Português"),
        new("ru", "Русский"),
        new("tr", "Türkçe"),
        new("zh", "简体中文"),
        new("ar", "العربية")
    ];


    public static void Apply(string? languageCode)
    {
        var code = string.IsNullOrWhiteSpace(languageCode) ? "en" : languageCode;
        if (!AvailableLanguages.Any(l => l.Code == code))
        {
            code = "en";
        }

        CurrentLanguage = code;

        var uri = new Uri($"/Localization/Languages/{code}.xaml", UriKind.Relative);
        var dict = new ResourceDictionary { Source = uri };

        var app = Application.Current;
        var existing = app.Resources.MergedDictionaries
            .FirstOrDefault(d => d.Source?.OriginalString.Contains("/Languages/") == true);

        if (existing is not null)
        {
            app.Resources.MergedDictionaries.Remove(existing);
        }

        app.Resources.MergedDictionaries.Add(dict);

        var flow = IsRtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        foreach (Window win in app.Windows)
        {
            win.FlowDirection = flow;
        }

        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string Get(string key, string? fallback = null)
    {
        var app = Application.Current;
        if (app?.Resources?.Contains(key) == true && app.Resources[key] is string s)
        {
            return s;
        }

        return fallback ?? key;
    }
}

public sealed record LanguageOption(string Code, string DisplayName);
