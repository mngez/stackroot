using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Stackroot.Core.Abstractions;

namespace Stackroot.Core.Dns;

public interface ITestDnsQueryLogger
{
    void Log(string transport, string? remoteEndPoint, string qname, ushort qtype, string disposition);
}

/// <summary>
/// JSONL query log (same file, same line format as always). Lines are queued to a
/// background writer so query handling never blocks on disk I/O — the old
/// open-append-close-per-line under a lock serialized the whole server under load.
/// </summary>
public sealed class TestDnsQueryLogger : ITestDnsQueryLogger, IDisposable
{
    private const int QueueCapacity = 4096;

    private readonly Channel<string> _lines = Channel.CreateBounded<string>(new BoundedChannelOptions(QueueCapacity)
    {
        // If disk can't keep up with a query flood, favor the newest lines and
        // keep serving DNS rather than stalling or growing without bound.
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true
    });

    private readonly string _logPath;
    private readonly Task _writerTask;
    private volatile bool _disposed;

    public TestDnsQueryLogger(StackrootPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.LogsRoot);
        _logPath = Path.Combine(paths.LogsRoot, "test-dns-queries.jsonl");
        _writerTask = Task.Run(WriteLoopAsync);
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

        _lines.Writer.TryWrite(line);
    }

    private async Task WriteLoopAsync()
    {
        while (await _lines.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            try
            {
                // Open per batch, not per line: a batch holds everything queued
                // since the last write, and FileShare.ReadWrite keeps the file
                // tailable from the app while the helper appends.
                await using var stream = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                await using var writer = new StreamWriter(stream, Encoding.UTF8);
                while (_lines.Reader.TryRead(out var line))
                {
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                }
            }
            catch
            {
                // Log file momentarily locked or unwritable — drop this batch and
                // retry with the next one; logging must never take DNS down.
                await Task.Delay(250).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lines.Writer.TryComplete();
        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }
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
