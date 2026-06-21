using System.Text;

namespace Stackroot.Core.Abstractions;

public static class DiagnosticsCounters
{
    private static int _snapshotCount;
    private static long _netstatInvocations;
    private static long _tcpTableInvocations;

    public static long LastRuntimeSnapshotMs { get; private set; }

    public static long NetstatInvocations => Interlocked.Read(ref _netstatInvocations);

    public static long TcpTableInvocations => Interlocked.Read(ref _tcpTableInvocations);

    public static event Action<string>? SummaryLogged;

    public static void RecordRuntimeSnapshot(long elapsedMs)
    {
        LastRuntimeSnapshotMs = elapsedMs;
        var count = Interlocked.Increment(ref _snapshotCount);
        if (count % 10 == 0)
        {
            EmitSummary();
        }
    }

    public static void RecordNetstatInvocation()
    {
        Interlocked.Increment(ref _netstatInvocations);
    }

    public static void RecordTcpTableInvocation()
    {
        Interlocked.Increment(ref _tcpTableInvocations);
    }

    public static string FormatSummary()
    {
        var builder = new StringBuilder();
        builder.Append("lastSnapshotMs=").Append(LastRuntimeSnapshotMs);
        builder.Append(", tcpTableCalls=").Append(TcpTableInvocations);
        builder.Append(", netstatCalls=").Append(NetstatInvocations);
        return builder.ToString();
    }

    public static void EmitSummary()
    {
        SummaryLogged?.Invoke(FormatSummary());
    }
}
