namespace Stackroot.Core.Services.Php;

/// <summary>
/// Resolves the Mozilla CA bundle shipped with Stackroot so PHP/cURL/OpenSSL
/// can verify HTTPS on Windows (where OpenSSL does not use the system store).
/// </summary>
public static class PhpCaBundle
{
    public const string FileName = "cacert.pem";

    public static string GetPath(string resourcesRoot)
        => Path.Combine(resourcesRoot, "packages", FileName);

    /// <summary>
    /// Returns the absolute CA bundle path when the file is present and non-empty; otherwise null.
    /// </summary>
    public static string? TryResolve(string resourcesRoot)
    {
        if (string.IsNullOrWhiteSpace(resourcesRoot))
        {
            return null;
        }

        var path = GetPath(resourcesRoot);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var info = new FileInfo(path);
            return info.Length > 1024 ? Path.GetFullPath(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Formats a filesystem path for php.ini (forward slashes, quoted).
    /// </summary>
    public static string ToIniValue(string absolutePath)
        => $"\"{Path.GetFullPath(absolutePath).Replace('\\', '/')}\"";
}
