namespace Stackroot.Core.Windows;

public interface IProcessJobManager
{
    void AssignProcess(int processId);
    Task ShutdownAsync(CancellationToken cancellationToken = default);
}
