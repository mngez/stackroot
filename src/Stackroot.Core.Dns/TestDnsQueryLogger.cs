using System.Text;
using System.Text.Json;
using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Dns;

public interface ITestDnsQueryLogger
{
    void Log(string transport, string? remoteEndPoint, string qname, ushort qtype, string disposition);
}

public sealed class TestDnsQueryLogger : ITestDnsQueryLogger, IDisposable
{
    private readonly object _gate = new();
    private readonly string _logPath;
    private bool _disposed;

    public TestDnsQueryLogger(StackrootPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.LogsRoot);
        _logPath = Path.Combine(paths.LogsRoot, "test-dns-queries.jsonl");
    }

    public string LogPath => _logPath;

    public void Log(string transport, string? remoteEndPoint, string qname, ushort qtype, string disposition)
    {
        if (_disposed)
        {
            return;
        }

        var line = JsonSerializer.Serialize(new
        {
            disposition,
            qname,
            qtype = FormatQtype(qtype),
            transport,
            remote = remoteEndPoint ?? string.Empty,
            at = DateTimeOffset.Now
        });

        lock (_gate)
        {
            File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8);
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private static string FormatQtype(ushort qtype) => qtype switch
    {
        1 => "A",
        28 => "AAAA",
        12 => "PTR",
        5 => "CNAME",
        16 => "TXT",
        _ => qtype.ToString()
    };
}
