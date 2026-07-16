using System.Buffers.Binary;

namespace Stackroot.Core.Dns;

/// <summary>
/// TTL-respecting cache for upstream (forwarded) DNS responses only. It is consulted
/// exclusively inside the forward branch, AFTER local-name matching has already
/// rejected the query — a configured local mapping (e.g. x.com → this IP) therefore
/// always wins over anything cached here, on every single query.
/// </summary>
public sealed class DnsForwardCache
{
    private const int MaxEntries = 4096;
    private const ushort FlagQr = 0x8000;
    private const ushort FlagTc = 0x0200;
    private const ushort FlagRd = 0x0100;
    private const ushort TypeOpt = 41;

    private static readonly TimeSpan MaxTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private sealed record Entry(byte[] Response, int QuestionLength, DateTimeOffset ExpiresAt);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public static string MakeKey(string qname, ushort qtype) =>
        qname.Trim().TrimEnd('.').ToLowerInvariant() + "|" + qtype;

    public void Flush()
    {
        lock (_gate)
        {
            _entries.Clear();
        }
    }

    public bool TryGet(string key, ReadOnlySpan<byte> query, int questionOffset, int questionLength, out byte[]? response)
    {
        response = null;
        Entry? entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out entry))
            {
                return false;
            }

            if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _entries.Remove(key);
                return false;
            }
        }

        // The cached packet is reused as-is, so the incoming question section must
        // occupy exactly the same bytes for compression pointers to stay valid.
        // Same name (case-insensitive) + same qtype means same length; anything
        // else falls through to a normal forward.
        if (questionOffset != 12
            || entry.QuestionLength != questionLength
            || query.Length < questionOffset + questionLength
            || entry.Response.Length < questionOffset + questionLength)
        {
            return false;
        }

        var copy = (byte[])entry.Response.Clone();

        // Stamp the client's transaction ID and its exact question bytes (0x20-style
        // case randomization must be echoed back verbatim or the client rejects us).
        query[..2].CopyTo(copy);
        query.Slice(questionOffset, questionLength).CopyTo(copy.AsSpan(questionOffset, questionLength));

        // Mirror the client's RD flag instead of the original requester's.
        var flags = BinaryPrimitives.ReadUInt16BigEndian(copy.AsSpan(2));
        var queryFlags = BinaryPrimitives.ReadUInt16BigEndian(query.Slice(2, 2));
        flags = (ushort)((flags & ~FlagRd) | (queryFlags & FlagRd));
        BinaryPrimitives.WriteUInt16BigEndian(copy.AsSpan(2), flags);

        response = copy;
        return true;
    }

    public void TryStore(string key, byte[] response, int questionOffset, int questionLength)
    {
        if (questionOffset != 12)
        {
            return;
        }

        var ttl = ComputeCacheTtl(response);
        if (ttl is not { } lifetime || lifetime <= TimeSpan.Zero)
        {
            return;
        }

        var entry = new Entry(response, questionLength, DateTimeOffset.UtcNow + lifetime);
        lock (_gate)
        {
            if (_entries.Count >= MaxEntries && !_entries.ContainsKey(key))
            {
                EvictLocked();
            }

            _entries[key] = entry;
        }
    }

    private void EvictLocked()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var expired in _entries.Where(pair => pair.Value.ExpiresAt <= now).Select(static pair => pair.Key).ToList())
        {
            _entries.Remove(expired);
        }

        if (_entries.Count < MaxEntries)
        {
            return;
        }

        foreach (var oldest in _entries
                     .OrderBy(static pair => pair.Value.ExpiresAt)
                     .Take(MaxEntries / 4)
                     .Select(static pair => pair.Key)
                     .ToList())
        {
            _entries.Remove(oldest);
        }
    }

    /// <summary>
    /// Cache lifetime for a response: the minimum record TTL (capped) for positive
    /// answers, a short fixed window for NXDOMAIN/NODATA, null for everything that
    /// must not be cached (errors, truncated or malformed packets, TTL 0).
    /// </summary>
    internal static TimeSpan? ComputeCacheTtl(byte[] response)
    {
        if (response.Length < 12)
        {
            return null;
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2));
        if ((flags & FlagQr) == 0 || (flags & FlagTc) != 0)
        {
            return null;
        }

        var rcode = flags & 0x000F;
        if (rcode == 3)
        {
            return NegativeTtl;
        }

        if (rcode != 0)
        {
            return null;
        }

        if (BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(4)) != 1)
        {
            return null;
        }

        var answerCount = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(6));
        var authorityCount = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(8));

        var offset = 12;
        if (!TrySkipName(response, ref offset) || offset + 4 > response.Length)
        {
            return null;
        }

        offset += 4;

        uint? minTtl = null;
        for (var i = 0; i < answerCount + authorityCount; i++)
        {
            if (!TrySkipName(response, ref offset) || offset + 10 > response.Length)
            {
                return null;
            }

            var type = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset));
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(offset + 4));
            var rdLength = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset + 8));
            offset += 10 + rdLength;
            if (offset > response.Length)
            {
                return null;
            }

            if (type != TypeOpt)
            {
                minTtl = minTtl is { } current ? Math.Min(current, ttl) : ttl;
            }
        }

        if (answerCount == 0)
        {
            // NODATA — cache briefly, honoring an even shorter SOA minimum when present.
            var negative = minTtl is { } soaTtl && soaTtl < NegativeTtl.TotalSeconds
                ? TimeSpan.FromSeconds(soaTtl)
                : NegativeTtl;
            return negative > TimeSpan.Zero ? negative : null;
        }

        if (minTtl is not { } seconds || seconds == 0)
        {
            return null;
        }

        var lifetime = TimeSpan.FromSeconds(seconds);
        return lifetime > MaxTtl ? MaxTtl : lifetime;
    }

    private static bool TrySkipName(byte[] packet, ref int offset)
    {
        while (offset < packet.Length)
        {
            var length = packet[offset];
            if (length == 0)
            {
                offset++;
                return true;
            }

            if ((length & 0xC0) == 0xC0)
            {
                offset += 2;
                return offset <= packet.Length;
            }

            offset += 1 + length;
        }

        return false;
    }
}
