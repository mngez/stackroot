using System.IO;
using System.Text.Json;
using System.Windows;
using Stackroot.Core.IO;

namespace Stackroot.App.Helpers;

internal static class LogDialogBoundsStore
{
    private const double DefaultWidth = 820;
    private const double DefaultHeight = 560;
    private const double MinWidth = 560;
    private const double MinHeight = 360;

    public static (double Width, double Height) Load()
    {
        try
        {
            var path = StorePath();
            if (!File.Exists(path))
            {
                return (DefaultWidth, DefaultHeight);
            }

            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<UiStateDocument>(json, JsonSerializerConfig.Default);
            if (state?.LogDialogWidth is null or <= 0 || state.LogDialogHeight is null or <= 0)
            {
                return (DefaultWidth, DefaultHeight);
            }

            return (ClampWidth(state.LogDialogWidth.Value), ClampHeight(state.LogDialogHeight.Value));
        }
        catch
        {
            return (DefaultWidth, DefaultHeight);
        }
    }

    public static void Save(double width, double height)
    {
        try
        {
            var path = StorePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var state = new UiStateDocument
            {
                LogDialogWidth = ClampWidth(width),
                LogDialogHeight = ClampHeight(height),
            };
            var json = JsonSerializer.Serialize(state, JsonSerializerConfig.Default);
            File.WriteAllText(path, json);
        }
        catch
        {
            // Best effort.
        }
    }

    private static string StorePath()
    {
        var dataRoot = StackrootPathResolver.Resolve(ensureDirectories: false).DataRoot;
        return Path.Combine(dataRoot, "ui-state.json");
    }

    private static double ClampWidth(double width)
    {
        var max = SystemParameters.WorkArea.Width;
        return Math.Clamp(width, MinWidth, max > 0 ? max : width);
    }

    private static double ClampHeight(double height)
    {
        var max = SystemParameters.WorkArea.Height;
        return Math.Clamp(height, MinHeight, max > 0 ? max : height);
    }

    private sealed class UiStateDocument
    {
        public double? LogDialogWidth { get; set; }

        public double? LogDialogHeight { get; set; }
    }
}
