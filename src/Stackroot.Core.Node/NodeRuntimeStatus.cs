namespace Stackroot.Core.Node;

public sealed record NodeRuntimeStatus
{
    public bool NvmInstalled { get; init; }
    public string? NvmVersion { get; init; }
    public string? ActiveVersion { get; init; }
    public string? NodeExecutablePath { get; init; }
    public IReadOnlyList<string> InstalledVersions { get; init; } = [];
    public string? Message { get; init; }
}
