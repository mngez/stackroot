using System.Diagnostics;
using System.Reflection;
using Stackroot.Core.Services;
using Stackroot.Core.Windows;
using Xunit;

namespace Stackroot.Core.Tests;

public sealed class ServiceProcessToolsStartTests
{
    [Fact]
    public void StartProcess_does_not_enable_stdio_redirection()
    {
        using var process = InvokeStartProcess(new RecordingJobManager(), "cmd.exe", ["/c", "exit", "0"]);
        process.WaitForExit(2000);

        Assert.False(process.StartInfo.RedirectStandardOutput);
        Assert.False(process.StartInfo.RedirectStandardError);
    }

    [Fact]
    public void StartProcess_assigns_process_to_job_manager()
    {
        var manager = new RecordingJobManager();
        using var process = InvokeStartProcess(manager, "cmd.exe", ["/c", "exit", "0"]);
        process.WaitForExit(2000);

        Assert.Equal(process.Id, manager.AssignedProcessId);
    }

    private static Process InvokeStartProcess(
        IProcessJobManager jobManager,
        string fileName,
        IReadOnlyList<string> arguments)
    {
        var type = typeof(ServiceManager).Assembly.GetType("Stackroot.Core.Services.ServiceProcessTools");
        Assert.NotNull(type);

        var method = type!.GetMethod(
            "StartProcess",
            BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method!.Invoke(null, [fileName, arguments, Environment.CurrentDirectory, jobManager, null]);
        Assert.NotNull(result);
        return Assert.IsType<Process>(result);
    }

    private sealed class RecordingJobManager : IProcessJobManager
    {
        public int? AssignedProcessId { get; private set; }

        public void AssignProcess(int processId)
        {
            AssignedProcessId = processId;
        }

        public Task ShutdownAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
