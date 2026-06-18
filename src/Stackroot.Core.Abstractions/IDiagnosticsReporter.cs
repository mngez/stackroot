namespace Stackroot.Core.Abstractions;

public interface IDiagnosticsReporter
{
    bool IsEnabled { get; }

    void LogActivity(string area, string message);

    void LogUserError(string area, string message);

    void LogException(string area, Exception exception);

    IDisposable BeginAction(string area, string action);
}

public sealed class NoOpDiagnosticsReporter : IDiagnosticsReporter
{
    public static NoOpDiagnosticsReporter Instance { get; } = new();

    public bool IsEnabled => false;

    public void LogActivity(string area, string message)
    {
    }

    public void LogUserError(string area, string message)
    {
    }

    public void LogException(string area, Exception exception)
    {
    }

    public IDisposable BeginAction(string area, string action) => EmptyDisposable.Instance;

    private sealed class EmptyDisposable : IDisposable
    {
        public static EmptyDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
