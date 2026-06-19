namespace Stackroot.Core.Services.Php;

public sealed class PhpProfileImportResult
{
    public bool Succeeded { get; set; }

    public string TargetVersionId { get; init; } = string.Empty;

    public List<string> InstalledPackages { get; } = [];

    public List<string> InstalledExtensions { get; } = [];

    public List<string> EnabledExtensions { get; } = [];

    public List<string> StartedServices { get; } = [];

    public List<string> Skipped { get; } = [];

    public List<string> Failed { get; } = [];

    public string Summary { get; set; } = string.Empty;

    public static string BuildSummary(PhpProfileImportResult result)
    {
        var parts = new List<string>();
        if (result.InstalledPackages.Count > 0)
        {
            parts.Add($"installed packages: {string.Join(", ", result.InstalledPackages)}");
        }

        if (result.InstalledExtensions.Count > 0)
        {
            parts.Add($"installed extensions: {string.Join(", ", result.InstalledExtensions)}");
        }

        if (result.StartedServices.Count > 0)
        {
            parts.Add($"started services: {string.Join(", ", result.StartedServices)}");
        }

        if (result.EnabledExtensions.Count > 0)
        {
            parts.Add($"enabled: {string.Join(", ", result.EnabledExtensions)}");
        }

        if (result.Skipped.Count > 0)
        {
            parts.Add($"skipped: {string.Join(", ", result.Skipped)}");
        }

        if (result.Failed.Count > 0)
        {
            parts.Add($"failed: {string.Join(", ", result.Failed)}");
        }

        return parts.Count == 0 ? "No changes were required." : string.Join("; ", parts);
    }
}

public delegate void PhpProfileProgressCallback(string message);
