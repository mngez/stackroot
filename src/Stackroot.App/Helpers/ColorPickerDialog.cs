using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;

namespace Stackroot.App.Helpers;

internal static class ColorPickerDialog
{
    public static string? PickHex(Window? owner, string? currentHex)
    {
        using var dialog = new ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            SolidColorOnly = true
        };

        if (CustomCommandChromeHelper.NormalizeHex(currentHex) is { } hex)
        {
            dialog.Color = System.Drawing.Color.FromArgb(
                Convert.ToByte(hex[1..3], 16),
                Convert.ToByte(hex[3..5], 16),
                Convert.ToByte(hex[5..7], 16));
        }

        var result = owner is null
            ? dialog.ShowDialog()
            : dialog.ShowDialog(new WpfWindowOwner(owner));

        if (result != DialogResult.OK)
        {
            return null;
        }

        var picked = dialog.Color;
        return $"#{picked.R:X2}{picked.G:X2}{picked.B:X2}";
    }

    public static MediaColor? TryParseMediaColor(string? hex)
    {
        hex = CustomCommandChromeHelper.NormalizeHex(hex);
        if (hex is null)
        {
            return null;
        }

        return MediaColor.FromRgb(
            Convert.ToByte(hex[1..3], 16),
            Convert.ToByte(hex[3..5], 16),
            Convert.ToByte(hex[5..7], 16));
    }

    private sealed class WpfWindowOwner : System.Windows.Forms.IWin32Window
    {
        private readonly IntPtr _handle;

        public WpfWindowOwner(Window window) =>
            _handle = new WindowInteropHelper(window).Handle;

        public IntPtr Handle => _handle;
    }
}
