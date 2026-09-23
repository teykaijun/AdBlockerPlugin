using System.Net;
using System.Net.Sockets;

namespace AdBlocker.Dns;

/// <summary>
/// A DNS server on UDP and TCP. Every request is handed to a callback that returns
/// the answer (or null to stay silent). Oversized UDP answers are truncated so the
/// client retries over TCP.
/// </summary>
/// <param name="acceptClient">
/// Which clients are answered. Null answers everyone, which is only safe while listening on
/// loopback; serving the local network passes <see cref="LocalNetwork.IsPrivate"/> here.
/// </param>
public sealed class DnsServer(
    IReadOnlyList<IPEndPoint> endpoints,
    Func<byte[], CancellationToken, Task<byte[]?>> handler,
    Func<IPAddress, bool>? acceptClient = null) : IAsyncDisposable
{
    // Stops Windows from failing the next receive with WSAECONNRESET after an ICMP "port unreachable".
    private const int SioUdpConnReset = -1744830452;

    private readonly CancellationTokenSource _stopping = new();
    private readonly List<Socket> _udpSockets = [];
    private readonly List<TcpListener> _tcpListeners = [];
    private readonly List<Task> _loops = [];
    private readonly SemaphoreSlim _pendingQueries = new(1024);
    private readonly SemaphoreSlim _tcpConnections = new(64);

    /// <summary>The addresses actually being served, with the real port if 0 was requested.</summary>
    public List<IPEndPoint> BoundEndpoints { get; } = [];

    /// <summary>Endpoints that could not be opened (for example IPv6 when it is disabled).</summary>
    public List<(IPEndPoint Endpoint, SocketException Error)> Failed { get; } = [];

    /// <summary>Opens the sockets. Throws if none of the endpoints could be opened.</summary>
    public void Start()
    {
        foreach (var endpoint in endpoints)
        {
            Socket? udp = null;
            TcpListener? tcp = null;
            try
            {
                udp = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
                udp.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
                udp.Bind(endpoint);
                var bound = (IPEndPoint)udp.LocalEndPoint!;

                tcp = new TcpListener(bound) { ExclusiveAddressUse = true };
                tcp.Start();

                _udpSockets.Add(udp);
                _tcpListeners.Add(tcp);
                BoundEndpoints.Add(bound);
            }
            catch (SocketException e)
            {
                udp?.Dispose();
                tcp?.Dispose();
                Failed.Add((endpoint, e));
            }
        }

        if (BoundEndpoints.Count == 0)
        {
            throw Failed.Count > 0 ? Failed[0].Error : new InvalidOperationException("No endpoints to listen on.");
        }

        var token = _stopping.Token;
        foreach (var socket in _udpSockets) _loops.Add(Task.Run(() => ServeUdpAsync(socket, token)));
        foreach (var listener in _tcpListeners) _loops.Add(Task.Run(() => AcceptTcpAsync(listener, token)));
    }

    private async Task ServeUdpAsync(Socket socket, CancellationToken token)
    {
        var buffer = new byte[65_535];
        EndPoint any = new IPEndPoint(socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (!token.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, token);
            }
            catch (SocketException)
            {
                continue;
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                break;
            }

            if (!Accepts(received.RemoteEndPoint)) continue;
            var query = buffer.AsSpan(0, received.ReceivedBytes).ToArray();
            if (!_pendingQueries.Wait(0)) continue; // overloaded: the client will retry
            _ = AnswerUdpAsync(socket, query, received.RemoteEndPoint, token);
        }
    }

    private bool Accepts(EndPoint? client) =>
        acceptClient is null || (client is IPEndPoint endpoint && acceptClient(endpoint.Address));

    private async Task AnswerUdpAsync(Socket socket, byte[] query, EndPoint client, CancellationToken token)
    {
        try
        {
            var answer = await handler(query, token);
            if (answer is null) return;
            if (DnsMessage.TryParseQuery(query, out var question) && answer.Length > DnsMessage.MaxUdpResponseSize(query, question))
            {
                answer = DnsMessage.TruncatedResponse(query, question);
            }
            await socket.SendToAsync(answer, SocketFlags.None, client, token);
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Shutting down, or the client went away.
        }
        finally
        {
            _pendingQueries.Release();
        }
    }

    private async Task AcceptTcpAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token);
            }
            catch (SocketException)
            {
                continue;
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                break;
            }

            if (!Accepts(client.Client.RemoteEndPoint) || !_tcpConnections.Wait(0))
            {
                client.Dispose();
                continue;
            }
            _ = ServeTcpAsync(client, token);
        }
    }

    private async Task ServeTcpAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                while (true)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                    idle.CancelAfter(TimeSpan.FromSeconds(10));
                    var query = await DnsTcp.ReadMessageAsync(stream, idle.Token);
                    if (query is null) return;
                    var answer = await handler(query, token);
                    if (answer is null) return;
                    await DnsTcp.WriteMessageAsync(stream, answer, token);
                }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Idle, closed by the client, or shutting down.
        }
        finally
        {
            _tcpConnections.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        foreach (var socket in _udpSockets) socket.Dispose();
        foreach (var listener in _tcpListeners) listener.Dispose();
        await Task.WhenAll(_loops);
        _stopping.Dispose();
    }
}
