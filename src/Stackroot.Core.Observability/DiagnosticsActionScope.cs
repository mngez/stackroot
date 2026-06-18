using System.Diagnostics;
using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Observability;

public sealed class DiagnosticsActionScope : IDisposable
{
    private readonly IDiagnosticsReporter _reporter;
    private readonly string _area;
    private readonly string _action;
    private readonly Stopwatch _stopwatch;
    private bool _disposed;
    private bool _failed;
    private string? _failureMessage;

    private DiagnosticsActionScope(IDiagnosticsReporter reporter, string area, string action)
    {
        _reporter = reporter;
        _area = area;
        _action = action;
        _stopwatch = Stopwatch.StartNew();
        _reporter.LogActivity(area, $"START {action}");
    }

    internal static DiagnosticsActionScope Create(IDiagnosticsReporter reporter, string area, string action)
        => new(reporter, area, action);

    public static IDisposable Begin(IDiagnosticsReporter reporter, string area, string action)
    {
        ArgumentNullException.ThrowIfNull(reporter);
        ArgumentException.ThrowIfNullOrWhiteSpace(area);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        if (!reporter.IsEnabled)
        {
            return NoOpDisposable.Instance;
        }

        return Create(reporter, area, action);
    }

    public void MarkFailed(string? message = null)
    {
        _failed = true;
        if (!string.IsNullOrWhiteSpace(message))
        {
            _failureMessage = message;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopwatch.Stop();
        if (_failed)
        {
            var suffix = string.IsNullOrWhiteSpace(_failureMessage) ? string.Empty : $": {_failureMessage}";
            _reporter.LogActivity(_area, $"FAILED {_action} ({_stopwatch.ElapsedMilliseconds}ms){suffix}");
            return;
        }

        _reporter.LogActivity(_area, $"DONE {_action} ({_stopwatch.ElapsedMilliseconds}ms)");
    }

    private sealed class NoOpDisposable : IDisposable
    {
        public static NoOpDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

public static class DiagnosticsReporterExtensions
{
    public static IDisposable BeginAction(this IDiagnosticsReporter reporter, string area, string action)
        => DiagnosticsActionScope.Begin(reporter, area, action);

    public static async Task RunActionAsync(
        this IDiagnosticsReporter reporter,
        string area,
        string action,
        Func<Task> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        using var scope = BeginTrackedScope(reporter, area, action);
        cancellationToken.ThrowIfCancellationRequested();
        await work().ConfigureAwait(false);
    }

    public static async Task<T> RunActionAsync<T>(
        this IDiagnosticsReporter reporter,
        string area,
        string action,
        Func<Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        using var scope = BeginTrackedScope(reporter, area, action);
        cancellationToken.ThrowIfCancellationRequested();
        return await work().ConfigureAwait(false);
    }

    public static async Task RunUserActionAsync(
        this IDiagnosticsReporter reporter,
        string area,
        string action,
        Func<Task> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        using var scope = BeginTrackedScope(reporter, area, action);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            scope.MarkFailed("cancelled");
            throw;
        }
        catch (Exception ex)
        {
            scope.MarkFailed(ex.Message);
            reporter.LogUserError(area, $"{action} failed: {ex.Message}");
            reporter.LogException(area, ex);
            throw;
        }
    }

    public static async Task<T> RunUserActionAsync<T>(
        this IDiagnosticsReporter reporter,
        string area,
        string action,
        Func<Task<T>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        using var scope = BeginTrackedScope(reporter, area, action);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            scope.MarkFailed("cancelled");
            throw;
        }
        catch (Exception ex)
        {
            scope.MarkFailed(ex.Message);
            reporter.LogUserError(area, $"{action} failed: {ex.Message}");
            reporter.LogException(area, ex);
            throw;
        }
    }

    private static TrackedActionScope BeginTrackedScope(
        IDiagnosticsReporter reporter,
        string area,
        string action)
        => TrackedActionScope.Begin(reporter, area, action);

    private sealed class TrackedActionScope : IDisposable
    {
        private readonly DiagnosticsActionScope? _scope;

        private TrackedActionScope(DiagnosticsActionScope? scope)
        {
            _scope = scope;
        }

        public static TrackedActionScope Begin(IDiagnosticsReporter reporter, string area, string action)
        {
            ArgumentNullException.ThrowIfNull(reporter);
            ArgumentException.ThrowIfNullOrWhiteSpace(area);
            ArgumentException.ThrowIfNullOrWhiteSpace(action);

            return reporter.IsEnabled
                ? new TrackedActionScope(DiagnosticsActionScope.Create(reporter, area, action))
                : new TrackedActionScope(null);
        }

        public void MarkFailed(string? message = null) => _scope?.MarkFailed(message);

        public void Dispose() => _scope?.Dispose();
    }
}
