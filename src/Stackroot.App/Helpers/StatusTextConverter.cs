using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using Stackroot.App.Localization;

namespace Stackroot.App.Helpers;

public sealed class StatusTextConverter : IValueConverter
{
    private static readonly Dictionary<string, string> KeyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Running"] = "Loc.Status.Running",
        ["Stopped"] = "Loc.Status.Stopped",
        ["Error"] = "Loc.Status.Error",
        ["Starting"] = "Loc.Status.Starting",
        ["Restarting"] = "Loc.Status.Restarting",
        ["Ready"] = "Loc.Status.Ready",
        ["Recovering"] = "Loc.Status.Recovering",
        ["Installing..."] = "Loc.Status.Installing",
        ["Disabled in settings"] = "Loc.Status.DisabledInSettings",
        ["Not installed"] = "Loc.Status.NotInstalled",
        ["Disabled"] = "Loc.Status.Disabled",
        ["Unavailable"] = "Loc.Status.Unavailable",
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text || string.IsNullOrEmpty(text))
            return value;

        if (KeyMap.TryGetValue(text, out var key))
            return LocalizationManager.Get(key, text);

        return value;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
