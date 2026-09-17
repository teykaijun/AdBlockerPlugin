using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace AdBlocker.Dns;

/// <summary>A DNS server that allowed queries are forwarded to.</summary>
public interface IUpstream
{
    string Name { get; }

    /// <summary>Sends <paramref name="query"/> and returns the raw answer.</summary>
    Task<byte[]> QueryAsync(byte[] query, CancellationToken cancellationToken);
}

/// <summary>Plain DNS over UDP, retried over TCP when the answer is truncated.</summary>
public sealed class UdpUpstream(IPEndPoint endpoint) : IUpstream
{
    private const int MaxMessage = 65_535;

    public UdpUpstream(IPAddress address) : this(new IPEndPoint(address, 53))
    {
    }

    public string Name => endpoint.Port == 53 ? endpoint.Address.ToString() : endpoint.ToString();

    public async Task<byte[]> QueryAsync(byte[] query, CancellationToken cancellationToken)
    {
        var answer = await QueryUdpAsync(query, cancellationToken);
        return DnsMessage.IsTruncated(answer) ? await QueryTcpAsync(query, cancellationToken) : answer;
    }

    private async Task<byte[]> QueryUdpAsync(byte[] query, CancellationToken cancellationToken)
    {
        using var socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        // A connected socket only accepts datagrams from the server itself.
        await socket.ConnectAsync(endpoint, cancellationToken);
        await socket.SendAsync(query, SocketFlags.None, cancellationToken);

        var buffer = ArrayPool<byte>.Shared.Rent(MaxMessage);
        try
        {
            while (true)
            {
                var received = await socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);
                var answer = buffer.AsSpan(0, received);
                if (DnsMessage.IsResponse(answer) && DnsMessage.ReadId(answer) == DnsMessage.ReadId(query))
                {
                    return answer.ToArray();
                }
                // Anything else is a stray datagram; keep waiting until the caller's deadline.
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<byte[]> QueryTcpAsync(byte[] query, CancellationToken cancellationToken)
    {
        using var client = new TcpClient(endpoint.AddressFamily);
        await client.ConnectAsync(endpoint, cancellationToken);
        var stream = client.GetStream();
        await DnsTcp.WriteMessageAsync(stream, query, cancellationToken);
        return await DnsTcp.ReadMessageAsync(stream, cancellationToken)
            ?? throw new IOException($"{Name} closed the TCP connection without answering.");
    }
}

/// <summary>DNS over HTTPS (RFC 8484), so the network in between cannot read or change lookups.</summary>
public sealed class DohUpstream(Uri url, HttpClient http) : IUpstream
{
    private static readonly MediaTypeHeaderValue DnsMessageType = new("application/dns-message");

    public string Name => url.ToString();

    public async Task<byte[]> QueryAsync(byte[] query, CancellationToken cancellationToken)
    {
        var content = new ByteArrayContent(query);
        content.Headers.ContentType = DnsMessageType;
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = content,
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var answer = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!DnsMessage.IsResponse(answer)) throw new InvalidDataException($"{Name} did not return a DNS message.");
        return answer;
    }

    /// <summary>
    /// An HTTP client for DNS-over-HTTPS servers. Server names are looked up with plain DNS
    /// against public resolvers instead of Windows, because Windows asks AdBlocker itself.
    /// </summary>
    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        ConnectCallback = ConnectAsync,
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        ConnectTimeout = TimeSpan.FromSeconds(5),
        UseProxy = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(10),
        DefaultRequestHeaders = { { "User-Agent", $"AdBlocker-Windows/{AppInfo.Version}" } },
    };

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        IReadOnlyList<IPAddress> addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await BootstrapResolver.ResolveAsync(host, cancellationToken);

        Exception? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                lastError = e;
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        throw new HttpRequestException($"Could not connect to {host}.", lastError);
    }
}

/// <summary>Resolves DNS-over-HTTPS server names without going through Windows.</summary>
internal static class BootstrapResolver
{
    private static readonly IUpstream[] Servers =
    [
        new UdpUpstream(IPAddress.Parse("1.1.1.1")),
        new UdpUpstream(IPAddress.Parse("9.9.9.9")),
        new UdpUpstream(IPAddress.Parse("8.8.8.8")),
    ];

    private static readonly UpstreamPool Pool = new(Servers, TimeSpan.FromSeconds(2));
    private static readonly ConcurrentDictionary<string, (IReadOnlyList<IPAddress> Addresses, DateTime Expires)> Cache = new();

    public static async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (Cache.TryGetValue(host, out var cached) && cached.Expires > DateTime.UtcNow) return cached.Addresses;

        var addresses = new List<IPAddress>();
        foreach (var type in new[] { DnsMessage.TypeA, DnsMessage.TypeAaaa })
        {
            var query = DnsMessage.BuildQuery((ushort)Random.Shared.Next(ushort.MaxValue), host, type);
            if (await Pool.QueryAsync(query, cancellationToken) is { } answer) addresses.AddRange(DnsMessage.ReadAddresses(answer));
        }
        if (addresses.Count == 0) throw new HttpRequestException($"Could not look up {host}.");

        Cache[host] = (addresses, DateTime.UtcNow.AddMinutes(10));
        return addresses;
    }
}

/// <summary>Tries servers in order, remembering the last one that worked.</summary>
public sealed class UpstreamPool(IReadOnlyList<IUpstream> servers, TimeSpan attemptTimeout)
{
    private const int MaxAttempts = 3;
    private int _preferred;

    public IReadOnlyList<IUpstream> Servers => servers;

    /// <summary>The answer, or null when no server answered in time.</summary>
    public async Task<byte[]?> QueryAsync(byte[] query, CancellationToken cancellationToken)
    {
        if (servers.Count == 0) return null;
        var start = Volatile.Read(ref _preferred) % servers.Count;
        for (var attempt = 0; attempt < Math.Min(servers.Count, MaxAttempts); attempt++)
        {
            var index = (start + attempt) % servers.Count;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(attemptTimeout);
            try
            {
                var answer = await servers[index].QueryAsync(query, timeout.Token).WaitAsync(timeout.Token);
                if (attempt > 0) Volatile.Write(ref _preferred, index);
                return answer;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Timed out or failed: try the next server.
            }
        }
        return null;
    }
}

/// <summary>Length-prefixed DNS messages over TCP (RFC 1035 section 4.2.2).</summary>
internal static class DnsTcp
{
    public static async Task WriteMessageAsync(Stream stream, ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
    {
        var frame = new byte[message.Length + 2];
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)message.Length);
        message.CopyTo(frame.AsMemory(2));
        await stream.WriteAsync(frame, cancellationToken);
    }

    /// <summary>The next message, or null if the connection ended cleanly.</summary>
    public static async Task<byte[]?> ReadMessageAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[2];
        var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        if (read == 0) return null;
        if (read < header.Length) throw new EndOfStreamException();
        var message = new byte[BinaryPrimitives.ReadUInt16BigEndian(header)];
        await stream.ReadExactlyAsync(message, cancellationToken);
        return message;
    }
}
