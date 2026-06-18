namespace Stackroot.Core.Abstractions;

public interface IGlobalProcessArgvResolver
{
    IReadOnlyList<string> Resolve(GlobalProcess process);

    IReadOnlyDictionary<string, string?> BuildEnvironment(GlobalProcess process);

    string FormatDisplayCommandLine(GlobalProcess process, IReadOnlyList<string> resolvedArgv);

    string ResolveWorkDir(GlobalProcess process);
}
