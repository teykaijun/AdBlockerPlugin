using System.Net;
using System.Net.Sockets;
using AdBlocker.Config;
using AdBlocker.Dns;

namespace AdBlocker.Tests;

internal static class TestPaths
{
    public static string RepositoryRoot { get; } = FindRoot();

    /// <summary>A throw-away settings folder without the production access restrictions.</summary>
    public static AppPaths Temporary() =>
        new(Path.Combine(Path.GetTempPath(), "adblocker-tests", Guid.NewGuid().ToString("N")), restrictAccess: false);

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "filters", "lists.json"))) return dir.FullName;
        }
        throw new InvalidOperationException("Could not find the repository root.");
    }
}

/// <summary>Builds and reads DNS messages for assertions.</summary>
internal static class TestDns
{
    /// <summary>A query with an EDNS OPT record advertising <paramref name="udpSize"/>.</summary>
    public static byte[] QueryWithEdns(string name, ushort type = DnsMessage.TypeA, ushort id = 0x1234, ushort udpSize = 1232)
    {
        var query = DnsMessage.BuildQuery(id, name, type);
        var opt = new byte[11];
        opt[2] = 41; // TYPE OPT (0x0029)
        opt[3] = (byte)(udpSize >> 8);
        opt[4] = (byte)udpSize;
        var message = query.Concat(opt).ToArray();
        message[11] = 1; // ARCOUNT
        return message;
    }

    /// <summary>An answer to <paramref name="query"/> with one A record per address.</summary>
    public static byte[] Answer(byte[] query, params string[] addresses)
    {
        Assert.True(DnsMessage.TryParseQuery(query, out var question));
        var answer = new List<byte>(query[..question.End]);
        answer[2] = 0x81; // QR, RD
        answer[3] = 0x80; // RA
        answer[6] = 0;
        answer[7] = (byte)addresses.Length;
        answer[10] = 0;
        answer[11] = 0;
        foreach (var address in addresses)
        {
            answer.AddRange([0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4]);
            answer.AddRange(IPAddress.Parse(address).GetAddressBytes());
        }
        return [.. answer];
    }

    public static async Task<byte[]> QueryUdpAsync(IPEndPoint server, byte[] query)
    {
        using var client = new UdpClient(server.AddressFamily);
        client.Connect(server);
        await client.SendAsync(query);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await client.ReceiveAsync(timeout.Token);
        return result.Buffer;
    }

    public static async Task<byte[]> QueryTcpAsync(IPEndPoint server, byte[] query)
    {
        using var client = new TcpClient(server.AddressFamily);
        await client.ConnectAsync(server);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stream = client.GetStream();
        await DnsTcp.WriteMessageAsync(stream, query, timeout.Token);
        return await DnsTcp.ReadMessageAsync(stream, timeout.Token) ?? throw new IOException("No answer");
    }

    public static int FreePort()
    {
        using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)udp.LocalEndPoint!).Port;
    }
}

/// <summary>A pretend upstream DNS server on 127.0.0.1 that answers every name with 192.0.2.10.</summary>
internal sealed class FakeUpstream : IAsyncDisposable
{
    private const int SioUdpConnReset = -1744830452;
    private readonly Socket _udp = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
    private readonly TcpListener _tcp;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task[] _loops;
    private int _udpQueries;
    private int _tcpQueries;

    /// <param name="truncateUdp">Mark every UDP answer as truncated, so clients must retry over TCP.</param>
    public FakeUpstream(bool truncateUdp = false)
    {
        _udp.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        _udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        Endpoint = (IPEndPoint)_udp.LocalEndPoint!;
        _tcp = new TcpListener(Endpoint);
        _tcp.Start();
        _loops = [Task.Run(() => ServeUdpAsync(truncateUdp)), Task.Run(ServeTcpAsync)];
    }

    public IPEndPoint Endpoint { get; }

    public int UdpQueries => _udpQueries;

    public int TcpQueries => _tcpQueries;

    private async Task ServeUdpAsync(bool truncate)
    {
        var buffer = new byte[65_535];
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                var received = await _udp.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _stopping.Token);
                Interlocked.Increment(ref _udpQueries);
                var answer = TestDns.Answer(buffer[..received.ReceivedBytes], "192.0.2.10");
                if (truncate) answer[2] |= 0x02;
                await _udp.SendToAsync(answer, SocketFlags.None, received.RemoteEndPoint);
            }
            catch (SocketException)
            {
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
        }
    }

    private async Task ServeTcpAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            try
            {
                using var client = await _tcp.AcceptTcpClientAsync(_stopping.Token);
                var stream = client.GetStream();
                if (await DnsTcp.ReadMessageAsync(stream, _stopping.Token) is { } query)
                {
                    Interlocked.Increment(ref _tcpQueries);
                    await DnsTcp.WriteMessageAsync(stream, TestDns.Answer(query, "192.0.2.10"), _stopping.Token);
                }
            }
            catch (Exception e) when (e is SocketException or IOException)
            {
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _udp.Dispose();
        _tcp.Dispose();
        await Task.WhenAll(_loops);
    }
}
