using Microsoft.Extensions.DependencyInjection;
using Stackroot.Core.Abstractions;
using Stackroot.Core.Services;

namespace Stackroot.App.Services;

public static class StackrootDiagnosticsHooks
{
    public static void Wire(IServiceProvider services)
    {
        var diagnostics = services.GetRequiredService<IDiagnosticsReporter>();
        PortProbe.FailureLogger = (_, exception) => diagnostics.LogException("PortProbe", exception);
    }
}
