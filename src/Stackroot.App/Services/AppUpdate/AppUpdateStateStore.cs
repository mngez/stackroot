using System.IO;
using System.Text.Json;
using Stackroot.Core.Abstractions;

namespace Stackroot.App.Services.AppUpdate;

public sealed class AppUpdateStateStore
{
    private readonly StackrootPaths _paths;
    private readonly object _sync = new();

    public AppUpdateStateStore(StackrootPaths paths)
    {
        _paths = paths;
    }

    public string? GetDismissedVersion()
    {
        lock (_sync)
        {
            var path = ResolvePath();
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                var json = File.ReadAllText(path);
                using var document = JsonDocument.Parse(json);
                return document.RootElement.TryGetProperty("dismissedVersion", out var value)
                    ? value.GetString()
                    : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public void SetDismissedVersion(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        lock (_sync)
        {
            var path = ResolvePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var payload = JsonSerializer.Serialize(new { dismissedVersion = version.Trim() });
            File.WriteAllText(path, payload);
        }
    }

    private string ResolvePath() => Path.Combine(_paths.DataRoot, "app-update.json");
}
