namespace Stackroot.Core.Dns;

public sealed class LocalDnsServerOptions
{
    public static LocalDnsServerOptions Default { get; } = new();

    public IReadOnlyList<string> Suffixes { get; init; } = [".test"];

    public IReadOnlyList<string> LocalNames { get; init; } = [];

    public string ResolveAddress { get; init; } = LocalDnsResolveAddress.Default;

    public static LocalDnsServerOptions Create(
        IEnumerable<string>? suffixes,
        IEnumerable<string>? localNames,
        string? resolveAddress = null)
    {
        var allowDangerous = LocalDnsSuffix.ContainsCatchAll(suffixes);
        return new()
        {
            Suffixes = LocalDnsSuffix.NormalizeList(
                suffixes,
                ensureDefaultTest: !allowDangerous,
                allowDangerous: allowDangerous),
            LocalNames = LocalDnsCatalog.CollectNames(null, localNames ?? []),
            ResolveAddress = LocalDnsResolveAddress.Normalize(resolveAddress)
        };
    }
}
