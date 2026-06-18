namespace Stackroot.Core.Catalog;

public interface INpmTooling
{
    string? ResolveNpmCommand();

    IReadOnlyDictionary<string, string> BuildCommandEnvironment();
}
