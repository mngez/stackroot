namespace Stackroot.Core.Dns;

public static class LocalDnsCatalog
{
    public static IReadOnlyList<string> CollectNames(string? appDomain, IEnumerable<string> siteServerNames)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(appDomain))
        {
            names.Add(appDomain.Trim().ToLowerInvariant());
        }

        foreach (var name in siteServerNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            names.Add(name.Trim().ToLowerInvariant());
        }

        return names.ToList();
    }
}
