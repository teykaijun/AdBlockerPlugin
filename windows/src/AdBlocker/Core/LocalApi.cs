using System.Net;
using System.Net.Sockets;
using System.Text;
using AdBlocker.Filtering;

namespace AdBlocker.Core;

/// <summary>
/// A tiny HTTP endpoint on 127.0.0.1 that answers "is this host blocked?" for the
/// AdBlocker Companion browser extension, so it can close ad tabs and undo ad redirects.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><c>GET /v1/status</c> → <c>{"app":"AdBlocker","version":"…"}</c></item>
///   <item><c>GET /v1/check?host=ads.example.com</c> → <c>{"host":"ads.example.com","blocked":true,"scam":false}</c></item>
/// </list>
/// It only listens on loopback, only answers requests addressed to 127.0.0.1/localhost
/// (against DNS rebinding), refuses requests from web pages, and changes nothing.
/// </remarks>
public sealed class LocalApi : IAsyncDisposable
{
    /// <summary>
    /// Kept below Windows' dynamic port range (49152 and up), where outgoing connections and
    /// Hyper-V, WSL or Docker reservations could take it. The extension has the same number.
    /// </summary>
    public const int DefaultPort = 45353;
    private const int MaxRequestBytes = 8 * 1024;

    private readonly TcpListener _listener;
    private readonly Func<string, bool> _isBlocked;
    private readonly Func<string, bool> _isScam;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _connections = new(32);
    private readonly Task _loop;

    private LocalApi(TcpListener listener, Func<string, bool> isBlocked, Func<string, bool> isScam)
    {
        _listener = listener;
        _isBlocked = isBlocked;
        _isScam = isScam;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptAsync);
    }

    public int Port { get; }

    /// <summary>Starts listening, or returns null (after logging why) if the port is not available.</summary>
    public static LocalApi? TryStart(int port, Func<string, bool> isBlocked, Func<string, bool> isScam, ILog log)
    {
        var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
        try
        {
            listener.Start();
        }
        catch (SocketException e)
        {
            listener.Dispose();
            log.Warn($"The browser companion endpoint could not start on port {port}: {e.Message}");
            return null;
        }
        return new LocalApi(listener, isBlocked, isScam);
    }

    /// <summary>Whether AdBlocker's endpoint, in this or another process, answers on <paramref name="port"/>.</summary>
    public static async Task<bool> IsAnsweringAsync(int port)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
        try
        {
            var body = await http.GetStringAsync($"http://127.0.0.1:{port}/v1/status");
            return body.StartsWith("""{"app":"AdBlocker",""", StringComparison.Ordinal);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private async Task AcceptAsync()
    {
        var token = _stopping.Token;
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token);
            }
            catch (SocketException)
            {
                continue;
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                break;
            }

            if (!_connections.Wait(0))
            {
                client.Dispose();
                continue;
            }
            _ = ServeAsync(client, token);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken token)
    {
        try
        {
            using (client)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                var stream = client.GetStream();
                var head = await ReadHeadAsync(stream, timeout.Token);
                var (status, body) = head is null ? (400, """{"error":"bad request"}""") : Handle(head);
                await WriteResponseAsync(stream, status, body, timeout.Token);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Client went away, timed out, or we are shutting down.
        }
        finally
        {
            _connections.Release();
        }
    }

    /// <summary>Handles one request head (request line plus headers).</summary>
    internal (int Status, string Body) Handle(string head)
    {
        var lines = head.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            return (400, """{"error":"bad request"}""");
        }

        var headers = lines.Skip(1)
            .Select(l => l.Split(':', 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0].Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First()[1].Trim(), StringComparer.OrdinalIgnoreCase);

        // Only requests addressed to this machine by name or address, which stops DNS rebinding.
        var host = headers.GetValueOrDefault("Host");
        if (host != $"127.0.0.1:{Port}" && host != $"localhost:{Port}") return (403, """{"error":"forbidden"}""");

        // Browsers add Origin to requests made by web pages; only extensions may ask.
        if (headers.TryGetValue("Origin", out var origin) && !IsExtensionOrigin(origin)) return (403, """{"error":"forbidden"}""");

        if (requestLine[0] != "GET") return (405, """{"error":"method not allowed"}""");

        var target = requestLine[1];
        var question = target.IndexOf('?');
        var path = question >= 0 ? target[..question] : target;
        var query = question >= 0 ? target[(question + 1)..] : "";

        switch (path)
        {
            case "/v1/status":
                return (200, $$"""{"app":"AdBlocker","version":"{{AppInfo.Version}}"}""");
            case "/v1/check":
                var requested = QueryValue(query, "host");
                var name = requested is null ? null : RuleParser.NormalizeDomain(requested);
                if (name is null) return (400, """{"error":"host must be a domain name"}""");
                var blocked = _isBlocked(name);
                var scam = blocked && _isScam(name);
                return (200, $$"""{"host":"{{name}}","blocked":{{Json(blocked)}},"scam":{{Json(scam)}}}""");
            default:
                return (404, """{"error":"not found"}""");
        }
    }

    private static string Json(bool value) => value ? "true" : "false";

    private static bool IsExtensionOrigin(string origin) =>
        origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)
        || origin.StartsWith("moz-extension://", StringComparison.OrdinalIgnoreCase)
        || origin.StartsWith("extension://", StringComparison.OrdinalIgnoreCase);

    private static string? QueryValue(string query, string key)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts[0] != key) continue;
            try
            {
                return Uri.UnescapeDataString((parts.Length > 1 ? parts[1] : "").Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                return null;
            }
        }
        return null;
    }

    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[MaxRequestBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), token);
            if (read == 0) return null;
            length += read;
            var end = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8);
            if (end >= 0) return Encoding.ASCII.GetString(buffer, 0, end);
        }
        return null; // header too large
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string body, CancellationToken token)
    {
        var reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            403 => "Forbidden",
            404 => "Not Found",
            405 => "Method Not Allowed",
            _ => "Error",
        };
        var content = Encoding.UTF8.GetBytes(body);
        var headers =
            $"HTTP/1.1 {status} {reason}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {content.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            (status == 405 ? "Allow: GET\r\n" : "") +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), token);
        await stream.WriteAsync(content, token);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Stop();
        _listener.Dispose();
        await _loop;
        _stopping.Dispose();
    }
}
