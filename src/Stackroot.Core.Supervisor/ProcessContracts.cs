using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Supervisor;

public sealed record ProcessRunTarget(
    ProcessScope Scope,
    string Label,
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?>? EnvironmentVariables = null,
    bool Supervised = true,
    int? RestartDelaySeconds = null);

public sealed record ManagedProcessSnapshot(
    ProcessScope Scope,
    string Label,
    ProcessStatus Status,
    int? Pid,
    string CommandLine,
    string? Message,
    bool Supervised);
