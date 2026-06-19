using System.IO;
using System.Text.Json;
using Stackroot.Core.Abstractions;

namespace Stackroot.App.Services.SslTrust;

public sealed class SslTrustStateStore
{
    private readonly StackrootPaths _paths;
    private readonly object _sync = new();

    public SslTrustStateStore(StackrootPaths paths)
    {
        _paths = paths;
    }

    public string? GetDismissedCaThumbprint()
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
                return document.RootElement.TryGetProperty("dismissedCaThumbprint", out var value)
                    ? value.GetString()
                    : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public void SetDismissedCaThumbprint(string thumbprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbprint);
        lock (_sync)
        {
            var path = ResolvePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var payload = JsonSerializer.Serialize(new { dismissedCaThumbprint = thumbprint.Trim().ToUpperInvariant() });
            File.WriteAllText(path, payload);
        }
    }

    private string ResolvePath() => Path.Combine(_paths.DataRoot, "ssl-trust-prompt.json");
}
