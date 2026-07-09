using System.Text.Json.Serialization;

namespace Stackroot.Core.Dns;

public sealed class DnsHelperRuntimeConfig
{
    public const int SchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersionValue { get; set; } = SchemaVersion;

    [JsonPropertyName("dataRoot")]
    public string DataRoot { get; set; } = string.Empty;

    [JsonPropertyName("logsRoot")]
    public string LogsRoot { get; set; } = string.Empty;

    /// <summary>Test DNS enabled — NRPT rules should be present when true.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    /// <summary>Run the 127.0.0.1:53 listener. When false, NRPT routing is removed as well.</summary>
    [JsonPropertyName("listen")]
    public bool Listen { get; set; } = true;

    [JsonPropertyName("suffixes")]
    public List<string> Suffixes { get; set; } = [".test"];

    [JsonPropertyName("localNames")]
    public List<string> LocalNames { get; set; } = [];

    [JsonPropertyName("logRequests")]
    public bool LogRequests { get; set; }

    [JsonPropertyName("resolveAddress")]
    public string ResolveAddress { get; set; } = LocalDnsResolveAddress.Default;

    /// <summary>
    /// Set to a fresh value to force the helper to fully stop and rebind its
    /// listener socket, even if it already believes it's running. Config-only
    /// republishes (suffix/name changes, auto-start) must leave this unchanged
    /// so a wedged-but-still-"running" listener isn't mistaken for healthy.
    /// </summary>
    [JsonPropertyName("restartToken")]
    public Guid? RestartToken { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record class DnsHelperRuntimeStatus
{
    [JsonPropertyName("listenerRunning")]
    public bool ListenerRunning { get; set; }

    [JsonPropertyName("nrptActive")]
    public bool NrptActive { get; set; }

    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
