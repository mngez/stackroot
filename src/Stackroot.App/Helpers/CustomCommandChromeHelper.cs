using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Media = System.Windows.Media;

namespace Stackroot.App.Helpers;

internal static class CustomCommandChromeHelper
{
    public static bool HasCustomChrome(string? foregroundHex, string? backgroundHex, string? iconPath) =>
        !string.IsNullOrWhiteSpace(NormalizeHex(foregroundHex))
        || !string.IsNullOrWhiteSpace(NormalizeHex(backgroundHex))
        || !string.IsNullOrWhiteSpace(iconPath) && File.Exists(iconPath);

    public static string? NormalizeHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return null;
        }

        hex = hex.Trim();
        if (!hex.StartsWith('#'))
        {
            hex = "#" + hex;
        }

        return hex.Length == 7 ? hex.ToUpperInvariant() : null;
    }

    public static Media.Brush? TryBrush(string? hex)
    {
        hex = NormalizeHex(hex);
        if (hex is null)
        {
            return null;
        }

        var color = Media.Color.FromRgb(
            Convert.ToByte(hex[1..3], 16),
            Convert.ToByte(hex[3..5], 16),
            Convert.ToByte(hex[5..7], 16));
        var brush = new Media.SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static ImageSource? TryIcon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(Path.GetFullPath(path), UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
