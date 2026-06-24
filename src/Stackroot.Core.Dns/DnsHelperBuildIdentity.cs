using System.Security.Cryptography;

namespace Stackroot.Core.Dns;

public static class DnsHelperBuildIdentity
{
    public static bool FilesMatch(string leftPath, string rightPath)
    {
        if (!File.Exists(leftPath) || !File.Exists(rightPath))
        {
            return false;
        }

        var left = new FileInfo(leftPath);
        var right = new FileInfo(rightPath);
        if (left.Length != right.Length)
        {
            return false;
        }

        if (left.Length == 0)
        {
            return true;
        }

        return string.Equals(ComputeSha256Hex(leftPath), ComputeSha256Hex(rightPath), StringComparison.OrdinalIgnoreCase);
    }

    public static string? TryComputeSha256Hex(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        return ComputeSha256Hex(path);
    }

    private static string ComputeSha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash);
    }
}
