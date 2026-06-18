namespace Stackroot.Core.IO.Storage;

public static class DataRootPathResolver
{
    public static string Resolve(string rootDirectory, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return Path.Combine(rootDirectory, fileName);
    }
}
