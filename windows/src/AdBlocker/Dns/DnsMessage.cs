using System.Buffers.Binary;
using System.Net;

namespace AdBlocker.Dns;

/// <summary>The small part of the DNS wire format (RFC 1035) that a filtering forwarder needs.</summary>
public static class DnsMessage
{
    public const int HeaderSize = 12;
    public const ushort TypeA = 1;
    public const ushort TypeAaaa = 28;
    public const ushort TypeOpt = 41;
    public const int RcodeServFail = 2;
    public const int RcodeNxDomain = 3;

    private const int MaxNameLength = 253;
    private const int ClassicUdpLimit = 512;

    /// <param name="Name">Lower-cased query name without the trailing dot.</param>
    /// <param name="End">Offset of the first byte after the question.</param>
    public readonly record struct Question(ushort Id, string Name, ushort Type, int End);

    /// <summary>Reads the first question of a standard query. Returns false for anything else.</summary>
    public static bool TryParseQuery(ReadOnlySpan<byte> message, out Question question)
    {
        question = default;
        if (message.Length < HeaderSize) return false;
        var flags = ReadUInt16(message, 2);
        if ((flags & 0x8000) != 0) return false; // a response
        if (((flags >> 11) & 0x0F) != 0) return false; // not a standard query
        if (ReadUInt16(message, 4) == 0) return false; // no question

        Span<char> name = stackalloc char[MaxNameLength];
        var length = 0;
        var pos = HeaderSize;
        while (true)
        {
            if (pos >= message.Length) return false;
            int label = message[pos++];
            if (label == 0) break;
            // Larger values are compression pointers, never valid in the first name of a query.
            if (label > 63 || pos + label > message.Length) return false;
            var separator = length > 0 ? 1 : 0;
            if (length + separator + label > MaxNameLength) return false;
            if (separator == 1) name[length++] = '.';
            foreach (var b in message.Slice(pos, label))
            {
                name[length++] = b is >= (byte)'A' and <= (byte)'Z' ? (char)(b + 32) : (char)b;
            }
            pos += label;
        }
        if (pos + 4 > message.Length) return false;

        question = new Question(ReadUInt16(message, 0), new string(name[..length]), ReadUInt16(message, pos), pos + 4);
        return true;
    }

    /// <summary>NXDOMAIN answer that echoes the question and nothing else (EDNS options are dropped).</summary>
    public static byte[] BlockedResponse(ReadOnlySpan<byte> query, in Question question) =>
        Respond(query, question, RcodeNxDomain, truncated: false);

    /// <summary>SERVFAIL, sent when no upstream server answered, so the client fails fast.</summary>
    public static byte[] FailureResponse(ReadOnlySpan<byte> query, in Question question) =>
        Respond(query, question, RcodeServFail, truncated: false);

    /// <summary>Empty answer with the TC bit, telling the client to retry over TCP.</summary>
    public static byte[] TruncatedResponse(ReadOnlySpan<byte> query, in Question question) =>
        Respond(query, question, rcode: 0, truncated: true);

    private static byte[] Respond(ReadOnlySpan<byte> query, in Question question, int rcode, bool truncated)
    {
        var response = query[..question.End].ToArray();
        var recursionDesired = ReadUInt16(query, 2) & 0x0100;
        // QR (response) | TC | RD (copied) | RA (recursion available) | RCODE
        WriteUInt16(response, 2, 0x8000 | (truncated ? 0x0200 : 0) | recursionDesired | 0x0080 | rcode);
        WriteUInt16(response, 4, 1); // QDCOUNT
        WriteUInt16(response, 6, 0); // ANCOUNT
        WriteUInt16(response, 8, 0); // NSCOUNT
        WriteUInt16(response, 10, 0); // ARCOUNT
        return response;
    }

    /// <summary>The largest UDP answer the client accepts: its EDNS buffer size, or 512 bytes without EDNS.</summary>
    public static int MaxUdpResponseSize(ReadOnlySpan<byte> query, in Question question)
    {
        if (ReadUInt16(query, 4) != 1 || ReadUInt16(query, 6) != 0 || ReadUInt16(query, 8) != 0) return ClassicUdpLimit;
        var pos = question.End;
        int additional = ReadUInt16(query, 10);
        for (var i = 0; i < additional; i++)
        {
            if (!TrySkipName(query, ref pos) || pos + 10 > query.Length) return ClassicUdpLimit;
            var type = ReadUInt16(query, pos);
            if (type == TypeOpt) return Math.Max(ClassicUdpLimit, (int)ReadUInt16(query, pos + 2));
            pos += 10 + ReadUInt16(query, pos + 8);
        }
        return ClassicUdpLimit;
    }

    /// <summary>A and AAAA addresses from the answer section, used to look up DNS-over-HTTPS servers.</summary>
    public static List<IPAddress> ReadAddresses(ReadOnlySpan<byte> response)
    {
        var addresses = new List<IPAddress>();
        if (response.Length < HeaderSize) return addresses;
        var pos = HeaderSize;
        for (int i = 0, count = ReadUInt16(response, 4); i < count; i++)
        {
            if (!TrySkipName(response, ref pos)) return addresses;
            pos += 4;
        }
        for (int i = 0, count = ReadUInt16(response, 6); i < count; i++)
        {
            if (!TrySkipName(response, ref pos) || pos + 10 > response.Length) return addresses;
            var type = ReadUInt16(response, pos);
            int length = ReadUInt16(response, pos + 8);
            pos += 10;
            if (pos + length > response.Length) return addresses;
            if ((type == TypeA && length == 4) || (type == TypeAaaa && length == 16))
            {
                addresses.Add(new IPAddress(response.Slice(pos, length)));
            }
            pos += length;
        }
        return addresses;
    }

    /// <summary>Builds a standard recursive query for <paramref name="name"/>.</summary>
    public static byte[] BuildQuery(ushort id, string name, ushort type)
    {
        var labels = name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        var message = new byte[HeaderSize + labels.Sum(l => l.Length + 1) + 1 + 4];
        WriteUInt16(message, 0, id);
        WriteUInt16(message, 2, 0x0100); // RD
        WriteUInt16(message, 4, 1);
        var pos = HeaderSize;
        foreach (var label in labels)
        {
            if (label.Length > 63) throw new ArgumentException($"Label too long in {name}", nameof(name));
            message[pos++] = (byte)label.Length;
            foreach (var c in label) message[pos++] = (byte)c;
        }
        message[pos++] = 0;
        WriteUInt16(message, pos, type);
        WriteUInt16(message, pos + 2, 1); // class IN
        return message;
    }

    public static bool TrySkipName(ReadOnlySpan<byte> message, ref int pos)
    {
        while (pos < message.Length)
        {
            int length = message[pos];
            if (length == 0)
            {
                pos++;
                return true;
            }
            if ((length & 0xC0) == 0xC0)
            {
                pos += 2;
                return pos <= message.Length;
            }
            if (length > 63) return false;
            pos += 1 + length;
        }
        return false;
    }

    public static ushort ReadId(ReadOnlySpan<byte> message) => ReadUInt16(message, 0);

    public static void WriteId(Span<byte> message, ushort id) => WriteUInt16(message, 0, id);

    public static bool IsResponse(ReadOnlySpan<byte> message) => message.Length >= HeaderSize && (message[2] & 0x80) != 0;

    public static bool IsTruncated(ReadOnlySpan<byte> message) => message.Length >= HeaderSize && (message[2] & 0x02) != 0;

    public static int ResponseCode(ReadOnlySpan<byte> message) => message.Length >= HeaderSize ? message[3] & 0x0F : -1;

    public static string TypeName(ushort type) => type switch
    {
        1 => "A",
        2 => "NS",
        5 => "CNAME",
        6 => "SOA",
        12 => "PTR",
        15 => "MX",
        16 => "TXT",
        28 => "AAAA",
        33 => "SRV",
        64 => "SVCB",
        65 => "HTTPS",
        255 => "ANY",
        _ => $"TYPE{type}",
    };

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);

    private static void WriteUInt16(Span<byte> data, int offset, int value) =>
        BinaryPrimitives.WriteUInt16BigEndian(data[offset..], (ushort)value);
}
