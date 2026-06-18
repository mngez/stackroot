using System.Globalization;
using System.Text;

namespace Stackroot.Core.IO;

public static class FirstRunState
{
    public const string MarkerFileName = ".installed";

    public static string InstalledMarkerPath(string dataRoot) =>
        Path.Combine(dataRoot, MarkerFileName);

    public static bool IsFirstRun(string dataRoot)
    {
        if (File.Exists(InstalledMarkerPath(dataRoot)))
        {
            return false;
        }

        if (File.Exists(StackrootPathResolver.SettingsPath(dataRoot)))
        {
            MarkInstalled(dataRoot);
            return false;
        }

        return true;
    }

    public static void MarkInstalled(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        var path = InstalledMarkerPath(dataRoot);
        var lines = new[]
        {
            "Stackroot installation marker",
            $"installed-at: {DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)}",
            $"app-version: {GetAppVersion()}"
        };
        File.WriteAllText(path, string.Join(Environment.NewLine, lines), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string GetAppVersion() =>
        typeof(FirstRunState).Assembly.GetName().Version?.ToString() ?? "unknown";
}
