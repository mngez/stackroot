using Stackroot.Core.Abstractions;
using Stackroot.Core.Supervisor;

namespace Stackroot.Engine.Runtime;

public sealed class RuntimePhpListenerState
{
    public required string VersionId { get; init; }

    public required string Endpoint { get; init; }

    public int Port { get; init; }

    public bool IsRunning { get; init; }

    public string StatusText { get; init; } = "-";

    public int? Pid { get; init; }

    public bool IsRequired { get; init; }
}

public sealed class RuntimeProcessState
{
    public required string Id { get; init; }

    public ProcessStatus Status { get; init; }

    public bool Available { get; init; }

    public int? Pid { get; init; }
}

public sealed class RuntimeMailpitState
{
    public bool Enabled { get; init; }

    public bool Running { get; init; }

    public int? Pid { get; init; }

    public bool Installed { get; init; }
}

public sealed class RuntimeTestDnsState
{
    public bool Enabled { get; init; }

    public bool Running { get; init; }

    public bool NrptActive { get; init; }

    public string? Message { get; init; }
}

public sealed class RuntimeStateSnapshot
{
    public DateTimeOffset RefreshedAt { get; init; }

    public IReadOnlyList<ServiceInfo> Services { get; init; } = [];

    public IReadOnlyList<RuntimePhpListenerState> PhpListeners { get; init; } = [];

    public IReadOnlyList<RuntimeProcessState> Processes { get; init; } = [];

    public RuntimeMailpitState? Mailpit { get; init; }

    public RuntimeTestDnsState? TestDns { get; init; }
}
